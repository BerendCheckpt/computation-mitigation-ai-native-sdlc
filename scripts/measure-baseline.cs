#:project ../src/Cnu.AICO.Core/Cnu.AICO.Core.csproj
#:property PublishAot=false
// Measures how many output tokens a crystallized template actually avoids (.NET 10 file-based app).
//
// The same task runs N times in two arms, each in a fresh scratch project:
//   control  — no template tools; the model writes the code itself
//   template — the cnu-aico MCP server is available; the model can call generate_<id>
// Only runs that pass the same acceptance commands count (and template runs must really have used the template).
// Result: median output tokens per arm → `measuredOutputTokens` for spec.yaml, plus a raw evidence file.
//
//   dotnet run scripts/measure-baseline.cs -- --config measurements/minimal-api-endpoint.yaml [--runs 5] [--model claude-sonnet-5-5]
//
// Costs real tokens: about 2 × runs Claude sessions. Requires the Claude Code CLI (`claude`) and ANTHROPIC_API_KEY.
// For a dry run without a model: CNU_AICO_CLAUDE=bash CNU_AICO_CLAUDE_PREFIX=tests/measure-baseline/fake-claude.sh
// On Windows also set CNU_AICO_BASH (and CNU_AICO_CLAUDE) to Git Bash, e.g. "C:/Program Files/Git/bin/bash.exe".
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cnu.AICO.Core.Telemetry;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

string? Opt(string name) => Array.IndexOf(args, name) is var i && i >= 0 && i + 1 < args.Length ? args[i + 1] : null;

var repo = Directory.GetCurrentDirectory();
if (!File.Exists(Path.Combine(repo, "Cnu.AICO.slnx")))
    return Fail("Run from the repository root.");

var configPath = Opt("--config") ?? throw new ArgumentException("--config <measurements/<template>.yaml> is required");
var config = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build()
    .Deserialize<MeasureConfig>(File.ReadAllText(configPath));
var runs = int.Parse(Opt("--runs") ?? config.Runs.ToString());
var model = Opt("--model") ?? config.Model;
var outDir = Opt("--out") ?? "measurements";
var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
var claude = Environment.GetEnvironmentVariable("CNU_AICO_CLAUDE") ?? "claude";
var claudePrefix = Environment.GetEnvironmentVariable("CNU_AICO_CLAUDE_PREFIX");
var toolName = "generate_" + config.Template.Replace('-', '_');
// Shell for scaffold/acceptance commands. On Windows, point this at Git Bash (bash.exe on PATH may be the WSL launcher).
var shell = Environment.GetEnvironmentVariable("CNU_AICO_BASH") ?? "bash";

var root = Path.Combine(Path.GetTempPath(), "aico-measure", $"{config.Template}-{DateTime.UtcNow:yyyyMMddHHmmss}");
Directory.CreateDirectory(root);

// Build the server once, so per-run startup does not include a build.
var serverDir = Path.Combine(root, "server");
if (Run("dotnet", ["build", Path.Combine(repo, "src", "Cnu.AICO.McpServer"), "-c", "Release", "-o", serverDir, "-nologo", "-v", "q"], repo, TimeSpan.FromMinutes(5)).Exit != 0)
    return Fail("Building the MCP server failed.");

var results = new List<MeasurementRun>();
for (var i = 1; i <= runs; i++)
{
    // Alternate the arm order so time-of-day or rate-limit drift does not favour one arm.
    foreach (var arm in i % 2 == 1 ? new[] { Arms.Control, Arms.Template } : [Arms.Template, Arms.Control])
    {
        var run = MeasureOnce(arm, i);
        results.Add(run);
        Console.Error.WriteLine($"[{arm} #{i}] output={run.Usage?.OutputTokens.ToString() ?? "-"} accepted={run.Accepted} usedTemplate={run.UsedTemplate?.ToString() ?? "-"} {run.Problem}");
    }
}

var summary = TokenMeasurement.Summarize(config.Template, model, date, results);
Directory.CreateDirectory(outDir);
var evidenceFull = Path.GetFullPath(Path.Combine(outDir, $"{config.Template}-{date.Replace("-", "")}.json"));
// spec.yaml references evidence relative to the repo root.
var evidence = (evidenceFull.StartsWith(repo, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(repo, evidenceFull) : evidenceFull).Replace('\\', '/');
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
File.WriteAllText(evidenceFull, JsonSerializer.Serialize(new
{
    template = config.Template,
    model,
    date,
    prompt = config.Prompt,
    acceptance = config.Acceptance,
    method = "Median output tokens of accepted runs: control (no template tools) vs template (cnu-aico MCP server). Avoided = baseline - deterministic.",
    summary,
    runs = results,
}, json) + "\n");

Console.WriteLine();
Console.WriteLine($"| Arm | Accepted runs | Median output tokens |\n|---|---|---|");
Console.WriteLine($"| control (model writes code) | {summary.ControlAccepted}/{runs} | {summary.BaselineMedian?.ToString("N0") ?? "-"} |");
Console.WriteLine($"| template (generate tool) | {summary.TemplateAccepted}/{runs} | {summary.DeterministicMedian?.ToString("N0") ?? "-"} |");
Console.WriteLine($"\nEvidence: {evidence}");
if (!summary.IsUsable)
    return Fail(summary.Problem!);

var snippet = TokenMeasurement.ToSpecYaml(summary, evidence);
Console.WriteLine($"Avoided per run (measured): {summary.Avoided:N0} output tokens.\n\nAdd to templates/{config.Template}/spec.yaml after review:\n\n{snippet}");
if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } stepSummary)
    File.AppendAllText(stepSummary, $"## Token baseline: {config.Template}\n\nControl median **{summary.BaselineMedian:N0}**, template median **{summary.DeterministicMedian:N0}**, avoided **{summary.Avoided:N0}** output tokens per run (n={Math.Min(summary.ControlAccepted, summary.TemplateAccepted)}, {model}).\n\n```yaml\n{snippet}\n```\n");
if (Environment.GetEnvironmentVariable("GITHUB_OUTPUT") is { Length: > 0 } ghOut)
    File.AppendAllText(ghOut, $"evidence={evidence}\navoided={summary.Avoided}\n");
return 0;

MeasurementRun MeasureOnce(string arm, int index)
{
    var work = Path.Combine(root, $"{arm}-{index}");
    Directory.CreateDirectory(work);
    foreach (var cmd in config.Scaffold)
        if (Run(shell, ["-c", cmd], work, TimeSpan.FromMinutes(5)) is { Exit: not 0 } s)
            return new MeasurementRun(arm, index, null, false, null, $"scaffold failed: {cmd} ({s.Exit})");

    // The MCP config lives outside the work dir so it cannot influence the task.
    var mcpPath = Path.Combine(root, $"mcp-{arm}-{index}.json");
    var servers = new JsonObject();
    if (arm == Arms.Template)
        servers["cnu-aico"] = new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = "dotnet",
            ["args"] = new JsonArray(
                Path.Combine(serverDir, "Cnu.AICO.McpServer.dll"),
                "--templates", Path.Combine(repo, "templates"),
                "--policy", Path.Combine(repo, "policy.yaml"),
                "--log", Path.Combine(work, ".aico", "decisions.jsonl"),
                "--output-root", work,
                "--context", "build"),
        };
    File.WriteAllText(mcpPath, new JsonObject { ["mcpServers"] = servers }.ToJsonString());

    var allowed = "Read,Write,Edit,Glob,Grep,Bash(dotnet:*)";
    if (arm == Arms.Template)
        allowed += $",mcp__cnu-aico__match_template,mcp__cnu-aico__{toolName}";

    List<string> claudeArgs = [];
    if (!string.IsNullOrEmpty(claudePrefix))
        claudeArgs.Add(Path.GetFullPath(claudePrefix, repo));
    claudeArgs.AddRange(["-p", config.Prompt, "--output-format", "json", "--model", model, "--max-turns", config.MaxTurns.ToString(),
        "--permission-mode", "acceptEdits", "--strict-mcp-config", "--mcp-config", mcpPath, "--allowedTools", allowed]);

    var (exit, stdout, stderr) = Run(claude, claudeArgs, work, TimeSpan.FromMinutes(config.TimeoutMinutes));
    ClaudeUsage usage;
    try { usage = TokenMeasurement.ParseClaudeJson(stdout); }
    catch (Exception ex) when (ex is JsonException or InvalidOperationException)
    {
        return new MeasurementRun(arm, index, null, false, null, $"claude exit {exit}: no JSON result ({Trim(stderr)})");
    }
    if (usage.IsError || exit != 0)
        return new MeasurementRun(arm, index, usage, false, null, $"claude reported an error (exit {exit}).");

    foreach (var cmd in config.Acceptance)
        if (Run(shell, ["-c", cmd], work, TimeSpan.FromMinutes(10)).Exit != 0)
            return new MeasurementRun(arm, index, usage, false, null, $"acceptance failed: {cmd}");

    bool? usedTemplate = null;
    if (arm == Arms.Template)
    {
        var log = Path.Combine(work, ".aico", "decisions.jsonl");
        usedTemplate = File.Exists(log) && DecisionLog.Read(log).Any(e => e.Tool == toolName && e.Route == "deterministic");
    }
    return new MeasurementRun(arm, index, usage, true, usedTemplate, arm == Arms.Template && usedTemplate != true ? "template tool was not used" : null);
}

static (int Exit, string Stdout, string Stderr) Run(string file, IEnumerable<string> arguments, string cwd, TimeSpan timeout)
{
    var psi = new ProcessStartInfo(file) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in arguments)
        psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    if (!p.WaitForExit(timeout))
    {
        p.Kill(entireProcessTree: true);
        return (-1, stdout.Result, "timeout");
    }
    return (p.ExitCode, stdout.Result, stderr.Result);
}

static string Trim(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim() + "…";

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

sealed class MeasureConfig
{
    public string Template { get; set; } = "";
    public string Prompt { get; set; } = "";
    public int Runs { get; set; } = 5;
    public string Model { get; set; } = "claude-sonnet-5-5";
    public int MaxTurns { get; set; } = 40;
    public int TimeoutMinutes { get; set; } = 20;
    public List<string> Scaffold { get; set; } = [];
    public List<string> Acceptance { get; set; } = [];
}
