#:project ../src/Cnu.AICO.Core/Cnu.AICO.Core.csproj
#:property PublishAot=false
// Deterministic CI steps (.NET 10 file-based app). No model is involved in any of these.
//   dotnet run scripts/aico-ci.cs -- validate
//   dotnet run scripts/aico-ci.cs -- verify --base origin/main          → template_only=true|false (GITHUB_OUTPUT)
//   dotnet run scripts/aico-ci.cs -- match "<prompt>"                    → JSON routing decision
//   dotnet run scripts/aico-ci.cs -- generate minimal-api-endpoint --out <dir> entity=Order project=Shop.Api
//   dotnet run scripts/aico-ci.cs -- detect --metric ci_failure_rate --history ops/metrics/ci_failure_rate.json --current 0.08
//   dotnet run scripts/aico-ci.cs -- kpi --logs .aico --out reports/kpi.md
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Cnu.AICO.Core.Generation;
using Cnu.AICO.Core.Matching;
using Cnu.AICO.Core.Ops;
using Cnu.AICO.Core.Specs;
using Cnu.AICO.Core.Telemetry;

var command = args.FirstOrDefault() ?? "";
string? Opt(string name) => Array.IndexOf(args, name) is var i && i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var templatesPath = Opt("--templates") ?? "templates";
var log = new DecisionLog(Opt("--log") ?? Environment.GetEnvironmentVariable("CNU_AICO_LOG") ?? ".aico/decisions.jsonl");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

return command switch
{
    "validate" => Validate(),
    "verify" => Verify(),
    "match" => Match(),
    "generate" => Generate(),
    "detect" => Detect(),
    "kpi" => Kpi(),
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
        var avoided = results.Where(r => r.TemplateId is not null).Select(r => r.TemplateId!).Distinct()
            .Sum(id => registry.Find(id)?.Spec.EstimatedOutputTokens ?? 0);
        log.Append(new DecisionEntry { Tool = "template-verify", Route = Routes.Deterministic, Context = "ci", EstimatedTokensAvoided = avoided, Reason = "AI review skipped; regen-verified." });
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
    log.Append(new DecisionEntry { Tool = entry.Spec.ToolName, Route = Routes.Deterministic, TemplateId = id, Context = "ci", Confidence = 1,
        EstimatedTokensAvoided = entry.Spec.EstimatedOutputTokens - DecisionLog.ToolCallTokens });
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
    var md = KpiReport.Build(entries, TemplateRegistry.Load(templatesPath), kwh, grams);
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
    Console.Error.WriteLine("usage: aico-ci <validate|verify|match|generate|detect|kpi> [options]");
    return 2;
}
