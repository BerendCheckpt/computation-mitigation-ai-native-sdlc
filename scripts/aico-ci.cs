#:project ../src/Cnu.AICO.Core/Cnu.AICO.Core.csproj
#:property PublishAot=false
// Deterministic CI steps (.NET 10 file-based app). No model is involved in any of these.
//   dotnet run scripts/aico-ci.cs -- validate
//   dotnet run scripts/aico-ci.cs -- verify --base origin/main          → template_only=true|false (GITHUB_OUTPUT)
//   dotnet run scripts/aico-ci.cs -- match "<prompt>"                    → JSON routing decision
//   dotnet run scripts/aico-ci.cs -- generate minimal-api-endpoint --out <dir> entity=Order project=Shop.Api
//   dotnet run scripts/aico-ci.cs -- detect --metric ci_failure_rate --history ops/metrics/ci_failure_rate.json --current 0.08
//   dotnet run scripts/aico-ci.cs -- kpi --logs .aico --out reports/kpi.md
//   dotnet run scripts/aico-ci.cs -- ledger record --result <claude-result.json|session.jsonl> [--result <subagent.jsonl> …] --path model --source stage-6-agent-fix [--out <fragment.jsonl>]
//                                                                       (repeated --result files are summed: main transcript + sub-agent transcripts)
//   dotnet run scripts/aico-ci.cs -- ledger merge --from <dir-with-fragments>
//   dotnet run scripts/aico-ci.cs -- ledger report | ledger check      → reports/token-ledger.md (check: fails if stale)
//   dotnet run scripts/aico-ci.cs -- sonar [--url <sonar url>] [--project cnu-aico]
//                                                                       → bloat / conventions / bugs (no security); exit 2 on Blocker/High, 1 if unreachable
//                                                                         url: --url, else $SONAR_HOST_URL, else http://host.docker.internal:9000; token: $SONAR_TOKEN
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Cnu.AICO.Core.Generation;
using Cnu.AICO.Core.Matching;
using Cnu.AICO.Core.Ops;
using Cnu.AICO.Core.Specs;
using Cnu.AICO.Core.Telemetry;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var command = args.FirstOrDefault() ?? "";
string? Opt(string name) => Array.IndexOf(args, name) is var i && i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
List<string> Opts(string name) => [.. Enumerable.Range(0, Math.Max(0, args.Length - 1)).Where(i => args[i] == name).Select(i => args[i + 1])];
var templatesPath = Opt("--templates") ?? "templates";
var log = new DecisionLog(Opt("--log") ?? Environment.GetEnvironmentVariable("CNU_AICO_LOG") ?? ".aico/decisions.jsonl");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var ledgerPath = Opt("--ledger") ?? Environment.GetEnvironmentVariable("CNU_AICO_LEDGER") ?? TokenLedger.DefaultPath;

return command switch
{
    "validate" => Validate(),
    "verify" => Verify(),
    "match" => Match(),
    "generate" => Generate(),
    "detect" => Detect(),
    "kpi" => Kpi(),
    "ledger" => Ledger(),
    "sonar" => await Sonar(),
    _ => Usage(),
};

int Validate()
{
    var registry = TemplateRegistry.Load(templatesPath);
    var errors = registry.All.SelectMany(e => TemplateValidator.Validate(e)).ToList();
    foreach (var e in errors)
        Console.WriteLine($"::error::{e}");
    Console.WriteLine($"{registry.All.Count} template(s), {registry.Crystallized.Count()} crystallized, {errors.Count} error(s).");
    return errors.Count == 0 ? 0 : 1;
}

// Regenerates every template-generated file in the change set and compares. If *all* changed files verify,
// the PR is template-only and the AI review job is skipped — deterministic verification replaces a model call.
int Verify()
{
    var baseRef = Opt("--base") ?? "origin/main";
    var changed = Git("diff", "--name-only", "--diff-filter=ACMRD", $"{baseRef}...HEAD")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    var registry = TemplateRegistry.Load(templatesPath);
    var results = changed.Select(p => RegenVerifier.Verify(registry, ".", p)).ToList();

    var summary = new List<string> { "| File | Status | Detail |", "|---|---|---|" };
    summary.AddRange(results.Select(r => $"| `{r.Path}` | {r.Status} | {r.Detail} |"));
    foreach (var r in results.Where(r => r.Status is VerifyStatus.Mismatch or VerifyStatus.UnknownTemplate))
        Console.WriteLine($"::error file={r.Path}::{r.Detail}");
    foreach (var r in results.Where(r => r.Status == VerifyStatus.Outdated))
        Console.WriteLine($"::warning file={r.Path}::{r.Detail}");

    var templateOnly = RegenVerifier.IsTemplateOnly(results);
    var failed = RegenVerifier.HasFailures(results);
    Console.WriteLine(string.Join('\n', summary));
    Console.WriteLine($"template_only={templateOnly.ToString().ToLowerInvariant()} failures={failed.ToString().ToLowerInvariant()}");

    GitHubOutput("template_only", templateOnly ? "true" : "false");
    GitHubSummary("## Template verification\n\n" + string.Join('\n', summary) +
                  $"\n\n**Template-only change:** {(templateOnly ? "yes — AI review skipped, deterministic verification passed" : "no")}\n");
    if (templateOnly)
    {
        // Counts the skipped review only. Generation tokens were booked when the code was generated, and the
        // review tokens a skip saves are not measured, so no token figure is claimed here.
        log.Append(new DecisionEntry { Tool = "template-verify", Route = Routes.Deterministic, Context = "ci", TokensAvoided = 0, Reason = "AI review skipped; regen-verified." });
    }
    return failed ? 1 : 0;
}

int Match()
{
    var prompt = string.Join(' ', args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)));
    var m = new TemplateMatcher(TemplateRegistry.Load(templatesPath)).Match(prompt);
    Console.WriteLine(JsonSerializer.Serialize(m, json));
    GitHubOutput("route", m.Route);
    GitHubOutput("template", m.TemplateId ?? "");
    return 0;
}

// Runs a crystallized flow with no model and no MCP server (pipelines, scripts, engineers).
int Generate()
{
    var id = args.ElementAtOrDefault(1) ?? throw new ArgumentException("template id required");
    var entry = TemplateRegistry.Load(templatesPath).Find(id) ?? throw new ArgumentException($"Unknown template '{id}'.");
    if (!entry.IsCrystallized)
        throw new InvalidOperationException($"Template '{id}' is {entry.EffectiveState}; only crystallized templates run deterministically.");
    var parameters = args.Skip(2).Where(a => a.Contains('=') && !a.StartsWith("--", StringComparison.Ordinal))
        .Select(a => a.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
    var result = TemplateGenerator.Generate(entry.Spec, parameters);
    foreach (var path in TemplateGenerator.WriteTo(Opt("--out") ?? ".", result, overwrite: args.Contains("--overwrite")))
        Console.WriteLine(path);
    var avoided = entry.Spec.AvoidedOutputTokens(TokenLedger.Load(ledgerPath));
    log.Append(new DecisionEntry
    {
        Tool = entry.Spec.ToolName,
        Route = Routes.Deterministic,
        TemplateId = id,
        Context = "ci",
        Confidence = 1,
        TokensAvoided = avoided.Tokens,
        BaselineSource = avoided.Source
    });
    return 0;
}

int Detect()
{
    var metric = Opt("--metric") ?? "metric";
    var history = JsonSerializer.Deserialize<double[]>(File.ReadAllText(Opt("--history") ?? throw new ArgumentException("--history required")))!;
    var current = double.Parse(Opt("--current") ?? throw new ArgumentException("--current required"), CultureInfo.InvariantCulture);
    var d = AnomalyDetector.Evaluate(metric, history, current, higherIsWorse: Opt("--lower-is-worse") is null);
    Console.WriteLine(JsonSerializer.Serialize(new { d.Metric, d.Value, d.Mean, d.StdDev, d.Sigma, band = d.Band.ToString().ToLowerInvariant(), level = (int)d.Band }, json));
    GitHubOutput("band", d.Band.ToString().ToLowerInvariant());
    GitHubOutput("sigma", d.Sigma.ToString(CultureInfo.InvariantCulture));
    return 0;
}

int Kpi()
{
    var dir = Opt("--logs") ?? ".aico";
    var entries = Directory.Exists(dir)
        ? Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories).SelectMany(DecisionLog.Read)
        : [];
    var kwh = double.Parse(Environment.GetEnvironmentVariable("CNU_AICO_KWH_PER_MTOK") ?? "0.4", CultureInfo.InvariantCulture);
    var grams = double.Parse(Environment.GetEnvironmentVariable("CNU_AICO_G_CO2_PER_KWH") ?? "300", CultureInfo.InvariantCulture);
    var md = KpiReport.Build(entries, TemplateRegistry.Load(templatesPath), kwh, grams, TokenLedger.Load(ledgerPath));
    var outPath = Opt("--out");
    if (outPath is null)
        Console.WriteLine(md);
    else
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, md);
    }
    return 0;
}

// Token ledger (ledger/token-usage.jsonl): append-only, committed, audited through git history.
int Ledger()
{
    var reportPath = Opt("--report") ?? "reports/token-ledger.md";
    switch (args.ElementAtOrDefault(1))
    {
        case "record":
            {
                // Never fails the calling workflow: a missing or unreadable result is a warning, not an error.
                // Several --result files (main session transcript + sub-agent transcripts) are summed.
                var resultFiles = Opts("--result");
                var missing = resultFiles.Count == 0 ? "" : resultFiles.FirstOrDefault(f => !File.Exists(f));
                if (missing is not null)
                {
                    Console.WriteLine($"::warning::No Claude result file at '{missing}'; nothing recorded in the token ledger.");
                    return 0;
                }
                var resultFile = string.Join(", ", resultFiles);
                ClaudeRunResult r;
                try { r = ClaudeResultParser.Parse(resultFiles.Select(File.ReadAllText).ToList()); }
                catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException)
                {
                    Console.WriteLine($"::warning::Could not read usage from '{resultFile}': {ex.Message}");
                    return 0;
                }

                var prompt = Opt("--prompt")
                    ?? (Opt("--prompt-file") is { } pf && File.Exists(pf) ? File.ReadAllText(pf) : null)
                    ?? Environment.GetEnvironmentVariable("CNU_AICO_PROMPT");
                if (string.IsNullOrWhiteSpace(prompt))
                    prompt = null;
                var registry = TemplateRegistry.Load(templatesPath);
                var matcher = new TemplateMatcher(registry);
                // The cnu-aico server's decision log of this job says whether the run called a template tool:
                // if it did, this is a deterministic-path run of that template; otherwise a model-path run.
                var generated = Opt("--decisions") is { } d && File.Exists(d)
                    ? DecisionLog.Read(d).LastOrDefault(e => e.Tool.StartsWith("generate_", StringComparison.Ordinal) && e.Route == Routes.Deterministic)
                    : null;
                var usedTemplate = generated is not null;
                var path = Opt("--path") ?? (usedTemplate ? LedgerPaths.Deterministic : LedgerPaths.Model);
                // A model-path run is attributed to the matcher's best candidate, so that once that template is
                // crystallized, its first deterministic run finds this run as its baseline.
                var templateId = Opt("--template") ?? generated?.TemplateId ?? (prompt is null ? null : matcher.Match(prompt).TemplateId);

                var run = LedgerRun.From(r, TokenLedger.NewId("run"), path, Opt("--source") ?? "manual") with
                {
                    Context = Opt("--context") is { Length: > 0 } ctx ? ctx : null,
                    Prompt = TokenLedger.Preview(prompt),
                    PromptKey = prompt is null ? null : matcher.Normalizer.CacheKey(prompt),
                    TemplateId = templateId,
                    TemplateVersion = templateId is null ? null : registry.Find(templateId)?.Spec.Version,
                    UsedTemplate = usedTemplate,
                    Accepted = Opt("--accepted") is { } a ? bool.Parse(a) : null,
                    Ref = Opt("--ref") is { Length: > 0 } rf ? rf : null,
                    GitSha = GitSha(),
                    WorkflowRun = WorkflowRun(),
                };
                Console.WriteLine($"{run.Id}: {run.Path} {run.Source} output={run.Tokens.Output} total={run.Tokens.Total} cost≈{run.CostUsdEstimate} template={run.TemplateId ?? "-"}");
                GitHubOutput("run_id", run.Id);
                if (Opt("--out") is { } fragment)
                {
                    // Workflows write a fragment; the comparison happens when kpi-report merges it into the ledger.
                    TokenLedger.Load(fragment).Append(run);
                    return 0;
                }
                if (TokenLedger.Load(ledgerPath).Record(run) is { } cmp)
                    Console.WriteLine($"{cmp.Id}: saved {cmp.Saved.Output:N0} output tokens vs {cmp.BaselineRunId} ({cmp.Correspondence}, {(cmp.Accepted ? "accepted" : "rejected: " + cmp.Notes)})");
                return 0;
            }

        case "merge":
            {
                var from = Opt("--from") ?? throw new ArgumentException("--from <directory with *.jsonl fragments> is required");
                var ledger = TokenLedger.Load(ledgerPath);
                var incoming = Directory.Exists(from)
                    ? Directory.EnumerateFiles(from, "*.jsonl", SearchOption.AllDirectories).SelectMany(TokenLedger.ReadEntries).OrderBy(e => e.At).ToList()
                    : [];
                // Oldest first, through Record: a deterministic run is compared with the newest baseline that existed
                // before it, including model runs merged earlier in this same batch.
                var before = ledger.Entries.Count;
                var comparisons = 0;
                foreach (var e in incoming)
                {
                    if (e is LedgerRun run)
                        comparisons += ledger.Record(run) is null ? 0 : 1;
                    else
                        ledger.Append(e);
                }
                var added = ledger.Entries.Count - before;
                Console.WriteLine($"Merged {incoming.Count} fragment entr{(incoming.Count == 1 ? "y" : "ies")} into {ledgerPath}: {added} new ledger entries, {comparisons} comparison(s).");
                GitHubOutput("added", added.ToString(CultureInfo.InvariantCulture));
                return 0;
            }

        case "report":
            {
                var md = TokenLedger.Load(ledgerPath).Report(TemplateRegistry.Load(templatesPath));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
                File.WriteAllText(reportPath, md);
                Console.WriteLine($"Wrote {reportPath}.");
                return 0;
            }

        case "check":
            {
                var expected = TokenLedger.Load(ledgerPath).Report(TemplateRegistry.Load(templatesPath));
                var actual = File.Exists(reportPath) ? File.ReadAllText(reportPath).Replace("\r\n", "\n") : "";
                if (actual == expected)
                    return 0;
                Console.WriteLine($"::error file={reportPath}::{reportPath} is out of date with {ledgerPath}. Run: dotnet run scripts/aico-ci.cs -- ledger report");
                return 1;
            }

        default:
            Console.Error.WriteLine("usage: aico-ci ledger <record|merge|report|check> [options]");
            return 2;
    }
}

// Local only (never in a workflow): reads the whole project's open maintainability and reliability issues from the
// SonarQube server and groups them; classification lives in Cnu.AICO.Core.Ops.SonarFindings.
async Task<int> Sonar()
{
    var url = Opt("--url") ?? Environment.GetEnvironmentVariable("SONAR_HOST_URL");
    url = (string.IsNullOrWhiteSpace(url) ? "http://host.docker.internal:9000" : url).TrimEnd('/') + "/";
    var project = Opt("--project") ?? "cnu-aico";
    using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(30) };
    if (Environment.GetEnvironmentVariable("SONAR_TOKEN") is { Length: > 0 } token)
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    var issues = new List<SonarIssue>();
    for (var page = 1; ; page++)
    {
        string body;
        try
        {
            using var response = await http.GetAsync(SonarFindings.SearchPath(project, page));
            body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"SonarQube at {url} answered {(int)response.StatusCode} {response.ReasonPhrase} for project '{project}'. Check SONAR_TOKEN and --project.");
                return 1;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"Cannot reach SonarQube at {url}: {ex.Message}. Start it from the hub, or pass --url / set SONAR_HOST_URL.");
            return 1;
        }
        var result = SonarFindings.ParsePage(body);
        issues.AddRange(result.Issues);
        if (result.Issues.Count == 0 || page * SonarFindings.PageSize >= result.Total)
            break;
    }

    Console.Write(SonarFindings.Report(project, issues));
    return issues.Any(SonarFindings.IsBlocking) ? 2 : 0;
}

static string? GitSha()
{
    if (Environment.GetEnvironmentVariable("GITHUB_SHA") is { Length: > 0 } sha)
        return sha;
    try { return Git("rev-parse", "HEAD").Trim(); }
    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
}

static string? WorkflowRun() =>
    Environment.GetEnvironmentVariable("GITHUB_RUN_ID") is { Length: > 0 } id
        ? $"{Environment.GetEnvironmentVariable("GITHUB_SERVER_URL")}/{Environment.GetEnvironmentVariable("GITHUB_REPOSITORY")}/actions/runs/{id}"
        : null;

static string Git(params string[] gitArgs)
{
    using var p = Process.Start(new ProcessStartInfo("git", gitArgs) { RedirectStandardOutput = true })!;
    var output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return p.ExitCode == 0 ? output : throw new InvalidOperationException($"git {string.Join(' ', gitArgs)} failed ({p.ExitCode}).");
}

static void GitHubOutput(string name, string value)
{
    if (Environment.GetEnvironmentVariable("GITHUB_OUTPUT") is { Length: > 0 } path)
        File.AppendAllText(path, $"{name}={value}\n");
}

static void GitHubSummary(string markdown)
{
    if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } path)
        File.AppendAllText(path, markdown);
}

static int Usage()
{
    Console.Error.WriteLine("usage: aico-ci <validate|verify|match|generate|detect|kpi|ledger|sonar> [options]");
    return 2;
}
