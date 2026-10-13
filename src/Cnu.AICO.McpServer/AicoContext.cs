using Cnu.AICO.Core.Matching;
using Cnu.AICO.Core.Policy;
using Cnu.AICO.Core.Specs;
using Cnu.AICO.Core.Telemetry;

namespace Cnu.AICO.McpServer;

/// <summary>Server configuration: CLI flags win over environment variables, which win over defaults.</summary>
public sealed record AicoOptions
{
    public string TemplatesPath { get; init; } = "templates";
    public string PolicyPath { get; init; } = "policy.yaml";
    public string LogPath { get; init; } = ".aico/decisions.jsonl";

    /// <summary>Version-controlled token ledger; measured savings per template come from its accepted comparisons.</summary>
    public string LedgerPath { get; init; } = TokenLedger.DefaultPath;

    /// <summary>Repository root that generated files are written under.</summary>
    public string OutputRoot { get; init; } = ".";

    /// <summary>Context assumed for calls that do not pass one (e.g. "ci" in pipelines).</summary>
    public string? DefaultContext { get; init; }
    public IReadOnlyList<string> DefaultLabels { get; init; } = [];

    /// <summary>When set, serve MCP over HTTP on this URL instead of stdio.</summary>
    public string? HttpUrl { get; init; }

    public static AicoOptions From(string[] args, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var defaults = new AicoOptions();
        return new AicoOptions
        {
            TemplatesPath = Arg("--templates") ?? env("CNU_AICO_TEMPLATES") ?? defaults.TemplatesPath,
            PolicyPath = Arg("--policy") ?? env("CNU_AICO_POLICY") ?? defaults.PolicyPath,
            LogPath = Arg("--log") ?? env("CNU_AICO_LOG") ?? defaults.LogPath,
            LedgerPath = Arg("--ledger") ?? env("CNU_AICO_LEDGER") ?? defaults.LedgerPath,
            OutputRoot = Arg("--output-root") ?? env("CNU_AICO_OUTPUT_ROOT") ?? defaults.OutputRoot,
            DefaultContext = Arg("--context") ?? env("CNU_AICO_CONTEXT"),
            DefaultLabels = SplitLabels(Arg("--labels") ?? env("CNU_AICO_LABELS")),
            HttpUrl = Arg("--http") ?? env("CNU_AICO_HTTP"),
        };
    }

    public static IReadOnlyList<string> SplitLabels(string? labels) =>
        (labels ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Everything the tools share. Built once at startup; templates are files, so a restart picks up merged changes.</summary>
public sealed class AicoContext
{
    public AicoContext(AicoOptions options)
    {
        Options = options;
        Registry = TemplateRegistry.Load(options.TemplatesPath);
        Matcher = new TemplateMatcher(Registry);
        Policy = PolicyEvaluator.Load(options.PolicyPath);
        Log = new DecisionLog(options.LogPath);
        Outcomes = new OutcomeRecorder(Registry);
        Ledger = TokenLedger.Load(options.LedgerPath);
    }

    public AicoOptions Options { get; }
    public TemplateRegistry Registry { get; }
    public TemplateMatcher Matcher { get; }
    public PolicyEvaluator Policy { get; }
    public DecisionLog Log { get; }
    public OutcomeRecorder Outcomes { get; }
    public TokenLedger Ledger { get; }

    public PolicyDecision EvaluatePolicy(string? context, string? labels) =>
        Policy.Evaluate(context ?? Options.DefaultContext,
            Options.DefaultLabels.Concat(AicoOptions.SplitLabels(labels)));
}
