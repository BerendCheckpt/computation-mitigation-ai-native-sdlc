using System.Text.Json;
using System.Text.Json.Nodes;
using Cnu.AICO.Core.Generation;
using Cnu.AICO.Core.Matching;
using Cnu.AICO.Core.Specs;
using Cnu.AICO.Core.Telemetry;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cnu.AICO.McpServer;

/// <summary>
/// One MCP tool per crystallized template (<c>generate_&lt;id&gt;</c>). The template's parameter contract
/// is projected straight into the tool's JSON input schema, so a model only has to fill in a few fields
/// (~50 tokens) instead of writing the code (thousands of tokens).
/// </summary>
public sealed class GenerateTemplateTool : McpServerTool
{
    readonly AicoContext _aico;
    readonly string _templateId;

    public GenerateTemplateTool(AicoContext aico, TemplateSpec spec)
    {
        _aico = aico;
        _templateId = spec.Id;
        ProtocolTool = new Tool
        {
            Name = spec.ToolName,
            Title = spec.Title,
            Description = $"{spec.Title} (template {spec.Id}@{spec.Version}). Deterministic: writes code + unit tests + suggested integration tests; no code writing needed.",
            InputSchema = BuildInputSchema(spec),
            Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false },
        };
    }

    public override Tool ProtocolTool { get; }

    public override IReadOnlyList<object> Metadata => [];

    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var args = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        string? Str(string name) => args.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Bool(string name) => args.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.True;

        var entry = _aico.Registry.Find(_templateId);
        if (entry is null || !entry.IsCrystallized)
            return Error($"Template '{_templateId}' is no longer crystallized (state {entry?.EffectiveState}). Implement this change normally.");

        var policy = _aico.EvaluatePolicy(Str("context"), Str("labels"));
        if (!policy.AutonomousAllowed)
        {
            _aico.Log.Append(new DecisionEntry { Tool = ProtocolTool.Name, Route = Routes.Human, TemplateId = _templateId, Context = policy.Context, Reason = string.Join(" ", policy.Reasons) });
            return Error("Refused by policy: " + string.Join(" ", policy.Reasons));
        }

        var parameters = entry.Spec.Parameters
            .Where(p => Str(p.Name) is { Length: > 0 })
            .ToDictionary(p => p.Name, p => Str(p.Name)!);

        GenerationResult result;
        try
        {
            result = TemplateGenerator.Generate(entry.Spec, parameters);
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }

        var dryRun = Bool("dryRun");
        IReadOnlyList<string> written = [];
        if (!dryRun)
        {
            try
            {
                written = TemplateGenerator.WriteTo(_aico.Options.OutputRoot, result, Bool("overwrite"));
            }
            catch (InvalidOperationException ex)
            {
                return Error(ex.Message);
            }
        }

        var avoided = entry.Spec.AvoidedOutputTokens();
        _aico.Log.Append(new DecisionEntry
        {
            Tool = ProtocolTool.Name,
            Route = Routes.Deterministic,
            TemplateId = _templateId,
            Context = policy.Context,
            Confidence = 1,
            TokensAvoided = avoided.Tokens,
            BaselineSource = avoided.Source,
        });

        var summary = new JsonObject
        {
            ["template"] = $"{result.TemplateId}@{result.Version}",
            ["parameters"] = JsonSerializer.SerializeToNode(result.Parameters),
            ["dryRun"] = dryRun,
            ["files"] = new JsonArray(result.Files.Select(f => (JsonNode)new JsonObject { ["path"] = f.Path, ["kind"] = f.Kind.ToString() }).ToArray()),
            ["postActions"] = new JsonArray(result.PostActions.Select(a => (JsonNode)a).ToArray()),
            ["preconditions"] = new JsonArray(entry.Spec.Preconditions.Select(p => (JsonNode)p).ToArray()),
            ["requireHumanReview"] = policy.RequireHumanReview,
            ["next"] = "Run postActions, wire up per preconditions, adapt the suggested integration tests (delete their aico:suggested line). Do not edit aico:generated files by hand.",
        };
        if (dryRun)
            summary["contents"] = new JsonObject(result.Files.Select(f => KeyValuePair.Create(f.Path, (JsonNode?)f.Content)));

        return ValueTask.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = summary.ToJsonString() }],
            StructuredContent = JsonSerializer.SerializeToElement(summary),
        });
    }

    static ValueTask<CallToolResult> Error(string message) =>
        ValueTask.FromResult(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = message }] });

    static JsonElement BuildInputSchema(TemplateSpec spec)
    {
        var properties = new JsonObject();
        foreach (var p in spec.Parameters)
        {
            var prop = new JsonObject { ["type"] = "string", ["description"] = p.Description };
            if (p.Pattern is not null)
                prop["pattern"] = p.Pattern;
            if (p.Default is not null)
                prop["description"] = $"{p.Description} Default: {p.Default}";
            properties[p.Name] = prop;
        }
        properties["dryRun"] = new JsonObject { ["type"] = "boolean", ["description"] = "Return contents without writing files." };
        properties["overwrite"] = new JsonObject { ["type"] = "boolean", ["description"] = "Overwrite existing generated files." };
        properties["context"] = new JsonObject { ["type"] = "string", ["description"] = "Policy context, e.g. build, agent-fix, ci." };
        properties["labels"] = new JsonObject { ["type"] = "string", ["description"] = "Comma-separated issue labels." };

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(spec.Parameters.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray()),
            ["additionalProperties"] = false,
        };
        return JsonSerializer.SerializeToElement(schema);
    }
}
