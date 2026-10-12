using System.ComponentModel;
using System.Text.Json;
using Cnu.AICO.Core.Generation;
using Cnu.AICO.Core.Matching;
using Cnu.AICO.Core.Telemetry;
using ModelContextProtocol.Server;

namespace Cnu.AICO.McpServer;

/// <summary>
/// Static tools. Descriptions are deliberately short: they are sent to the model with every request,
/// so every word here costs tokens on every call.
/// </summary>
[McpServerToolType]
public sealed class AicoTools(AicoContext aico)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    [McpServerTool(Name = "match_template", ReadOnly = true, Idempotent = true)]
    [Description("Deterministic (no model) check whether a crystallized template fully serves a prompt. Call before writing code. route=deterministic → call the returned tool with the returned parameters.")]
    public string MatchTemplate(
        [Description("The user's request, verbatim.")] string prompt,
        [Description("ideation|feedback|build|agent-fix|diagnose|ci")] string? context = null,
        [Description("Comma-separated issue labels, e.g. critical.")] string? labels = null)
    {
        var policy = aico.EvaluatePolicy(context, labels);
        var match = aico.Matcher.Match(prompt);

        var route = !policy.AutonomousAllowed ? Routes.Human
            : match.IsDeterministic ? Routes.Deterministic
            : policy.ModelAllowed ? Routes.Model
            : Routes.Human;

        var spec = match.TemplateId is null ? null : aico.Registry.Find(match.TemplateId)?.Spec;
        aico.Log.Append(new DecisionEntry
        {
            Tool = "match_template",
            Route = route,
            TemplateId = match.TemplateId,
            Context = policy.Context,
            Confidence = match.Confidence,
            // Savings are booked by generate_* when the work actually happens, so one request is not counted twice.
            EstimatedTokensAvoided = 0,
            Reason = match.Reason,
        });

        return JsonSerializer.Serialize(new
        {
            route,
            tool = route == Routes.Deterministic ? match.ToolName : null,
            match.TemplateId,
            match.Parameters,
            match.MissingParameters,
            match.Confidence,
            match.Margin,
            match.ExactCacheHit,
            reason = route == Routes.Human ? string.Join(" ", policy.Reasons.DefaultIfEmpty(match.Reason)) : match.Reason,
            requireHumanReview = policy.RequireHumanReview,
            contextResource = spec is null ? null : $"aico://templates/{spec.Id}/context.md",
        }, Json);
    }

    [McpServerTool(Name = "export_script", ReadOnly = true, Idempotent = true)]
    [Description("Export a crystallized template run as a stand-alone bash or PowerShell script (no MCP, no model needed to re-run).")]
    public string ExportScript(
        [Description("Template id, e.g. minimal-api-endpoint.")] string templateId,
        [Description("Template parameters as name → value.")] Dictionary<string, string> parameters,
        [Description("bash | powershell")] string shell = "bash")
    {
        var entry = aico.Registry.Find(templateId) ?? throw new ArgumentException($"Unknown template '{templateId}'.");
        if (!entry.IsCrystallized)
            throw new InvalidOperationException($"Template '{templateId}' is {entry.EffectiveState}; only crystallized templates can be exported.");
        return ScriptExporter.Export(TemplateGenerator.Generate(entry.Spec, parameters), shell);
    }

    [McpServerTool(Name = "record_outcome", Destructive = false)]
    [Description("Record quality-gate results for a template run (feeds promotion, demotion and keyword learning).")]
    public string RecordOutcome(
        [Description("Template id.")] string templateId,
        [Description("Unique run id, e.g. the CI run id.")] string runId,
        [Description("Gate name → passed, e.g. {\"build\":true,\"unitTests\":true}.")] Dictionary<string, bool> gates,
        [Description("Prompt that produced the run, if any.")] string? prompt = null,
        [Description("Parameters used.")] Dictionary<string, string>? parameters = null,
        [Description("PR reference, e.g. org/repo#42.")] string? pullRequest = null)
    {
        var result = aico.Outcomes.Record(templateId, runId, gates, prompt, parameters, pullRequest);
        return JsonSerializer.Serialize(result, Json);
    }
}
