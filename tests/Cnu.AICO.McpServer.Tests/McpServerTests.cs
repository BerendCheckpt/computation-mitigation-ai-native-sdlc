using System.Text.Json;
using Cnu.AICO.Core.Generation;
using Cnu.AICO.Core.Specs;
using Cnu.AICO.Core.Telemetry;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Cnu.AICO.McpServer.Tests;

/// <summary>
/// End-to-end over a real stdio transport: the same path Claude Code (or the optional router) uses —
/// ListToolsRequest/Result and CallToolRequest/Result.
/// </summary>
public sealed class McpServerFixture : IAsyncLifetime
{
    readonly string _work = Path.Combine(Path.GetTempPath(), "aico-mcp-tests", Guid.NewGuid().ToString("N"));
    public McpClient Client { get; private set; } = null!;

    public string Templates => Path.Combine(_work, "templates");
    public string Output => Path.Combine(_work, "repo");
    public string LogPath => Path.Combine(_work, "decisions.jsonl");

    public async Task InitializeAsync()
    {
        CopyDir(Path.Combine(RepoRoot(), "templates"), Templates);
        // A second, not-yet-approved template must stay invisible to the model.
        CopyDir(Path.Combine(Templates, "minimal-api-endpoint"), Path.Combine(Templates, "draft-endpoint"));
        var draftSpec = Path.Combine(Templates, "draft-endpoint", "spec.yaml");
        File.WriteAllText(draftSpec, File.ReadAllText(draftSpec)
            .Replace("id: minimal-api-endpoint", "id: draft-endpoint")
            .Replace("state: crystallized", "state: candidate"));
        Directory.CreateDirectory(Output);

        Client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "cnu-aico",
            Command = "dotnet",
            Arguments =
            [
                typeof(AicoContext).Assembly.Location,
                "--templates", Templates,
                "--policy", Path.Combine(RepoRoot(), "policy.yaml"),
                "--log", LogPath,
                "--output-root", Output,
            ],
        }));
    }

    public async Task DisposeAsync() => await Client.DisposeAsync();

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cnu.AICO.slnx")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    static void CopyDir(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}

/// <summary>One server process for the class (xUnit runs a class's tests sequentially).</summary>
public sealed class McpServerTests(McpServerFixture f) : IClassFixture<McpServerFixture>
{
    McpClient _client => f.Client;
    string Templates => f.Templates;
    string Output => f.Output;
    string LogPath => f.LogPath;

    [Fact]
    public async Task ListTools_advertises_only_crystallized_templates()
    {
        var names = (await _client.ListToolsAsync()).Select(t => t.Name).ToList();

        Assert.Contains("match_template", names);
        Assert.Contains("export_script", names);
        Assert.Contains("record_outcome", names);
        Assert.Contains("generate_minimal_api_endpoint", names);
        Assert.DoesNotContain("generate_draft_endpoint", names);
    }

    [Fact]
    public async Task Generate_tool_schema_is_the_template_parameter_contract()
    {
        var tool = (await _client.ListToolsAsync()).Single(t => t.Name == "generate_minimal_api_endpoint");
        var schema = tool.ProtocolTool.InputSchema;

        Assert.Equal(["entity", "project"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.True(schema.GetProperty("properties").TryGetProperty("route", out _));
    }

    [Theory]
    [InlineData("Add a minimal API endpoint for entity Order in project Shop.Api", null, null, "deterministic")]
    [InlineData("Design a caching strategy for the label renderer", "ideation", null, "model")]
    [InlineData("Design a caching strategy for the label renderer", "ci", null, "human")]
    [InlineData("Fix flaky checkout test", "agent-fix", "critical", "human")]
    [InlineData("Fix flaky checkout test", "agent-fix", "bug", "model")]
    public async Task Match_template_routes(string prompt, string? context, string? labels, string expectedRoute)
    {
        var json = await Call("match_template", new() { ["prompt"] = prompt, ["context"] = context, ["labels"] = labels });
        Assert.Equal(expectedRoute, json.GetProperty("route").GetString());
    }

    [Fact]
    public async Task Generate_writes_regen_verifiable_files_and_logs_avoided_tokens()
    {
        var json = await Call("generate_minimal_api_endpoint", new() { ["entity"] = "Order", ["project"] = "Shop.Api" });

        var registry = TemplateRegistry.Load(Templates);
        var paths = json.GetProperty("files").EnumerateArray()
            .Where(f => f.GetProperty("kind").GetString() != "IntegrationSuggested")
            .Select(f => f.GetProperty("path").GetString()!).ToList();
        Assert.Equal(4, paths.Count);
        Assert.All(paths, p => Assert.Equal(VerifyStatus.Verified, RegenVerifier.Verify(registry, Output, p).Status));

        var entry = DecisionLog.Read(LogPath).Last();
        Assert.Equal("deterministic", entry.Route);
        Assert.True(entry.TokensAvoided > 3000);
        Assert.Equal("estimated", entry.BaselineSource);
    }

    [Fact]
    public async Task Generate_is_refused_for_critical_agent_fixes()
    {
        var result = await _client.CallToolAsync("generate_minimal_api_endpoint", new Dictionary<string, object?>
        {
            ["entity"] = "Refused", ["project"] = "Shop.Api", ["context"] = "agent-fix", ["labels"] = "critical",
        });

        Assert.True(result.IsError);
        Assert.Contains("critical", Text(result));
        Assert.False(Directory.Exists(Path.Combine(Output, "src", "Shop.Api", "Features", "Refuseds")));
    }

    [Fact]
    public async Task Generate_reports_missing_parameters_as_tool_errors()
    {
        var result = await _client.CallToolAsync("generate_minimal_api_endpoint", new Dictionary<string, object?> { ["entity"] = "Order" });

        Assert.True(result.IsError);
        Assert.Contains("project", Text(result));
    }

    [Fact]
    public async Task Export_script_reproduces_the_flow_without_a_model()
    {
        var result = await _client.CallToolAsync("export_script", new Dictionary<string, object?>
        {
            ["templateId"] = "minimal-api-endpoint",
            ["parameters"] = new Dictionary<string, string> { ["entity"] = "Order", ["project"] = "Shop.Api" },
            ["shell"] = "bash",
        });

        Assert.StartsWith("#!/usr/bin/env bash", Text(result));
    }

    [Fact]
    public async Task Template_context_is_served_as_a_resource()
    {
        var resources = await _client.ListResourcesAsync();
        Assert.Contains(resources, r => r.Uri == "aico://templates/minimal-api-endpoint/context.md");

        var read = await _client.ReadResourceAsync("aico://templates/minimal-api-endpoint/context.md");
        Assert.Contains("When NOT to use it", read.Contents.OfType<TextResourceContents>().Single().Text);
    }

    async Task<JsonElement> Call(string tool, Dictionary<string, object?> args)
    {
        var result = await _client.CallToolAsync(tool, args);
        Assert.True(result.IsError != true, Text(result));
        return JsonDocument.Parse(Text(result)).RootElement;
    }

    static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

}
