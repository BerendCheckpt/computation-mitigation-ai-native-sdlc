// OPTIONAL SAMPLE (ADR 0003): a deterministic front door that acts as an MCP client.
//
// Lesson-2 flow ("MCP clients"): app → MCP client → MCP server. Normally the app sends the user query plus the
// ListTools result to Claude, and Claude decides which tool to call. For a crystallized flow that decision is
// already known, so this router asks the server's deterministic matcher and sends the CallToolRequest itself:
// zero model tokens, including the tool-definition tokens that would accompany every model request.
//
//   dotnet run --project samples/RouterClient -- "Add a minimal API endpoint for entity Order in project Shop.Api"
//   dotnet run --project samples/RouterClient -- --dry-run "..."     (show what would be generated, write nothing)
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var dryRun = args.Contains("--dry-run");
var prompt = string.Join(' ', args.Where(a => a != "--dry-run"));
if (prompt.Length == 0)
{
    Console.Error.WriteLine("usage: RouterClient [--dry-run] \"<prompt>\"");
    return 2;
}

// The server command is configurable so the router works against a locally built server, a published binary or a container.
var serverCommand = Environment.GetEnvironmentVariable("CNU_AICO_SERVER_COMMAND") ?? "dotnet";
var serverArgs = Environment.GetEnvironmentVariable("CNU_AICO_SERVER_ARGS")?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
    ?? ["run", "--project", "src/Cnu.AICO.McpServer", "--no-build", "--"];

await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "cnu-aico",
    Command = serverCommand,
    Arguments = serverArgs,
}));

// 1. Deterministic routing decision (BM25 + slots + policy) — no model.
var match = Json(await client.CallToolAsync("match_template", new Dictionary<string, object?>
{
    ["prompt"] = prompt,
    ["context"] = Environment.GetEnvironmentVariable("CNU_AICO_CONTEXT"),
    ["labels"] = Environment.GetEnvironmentVariable("CNU_AICO_LABELS"),
}));

var route = match.GetProperty("route").GetString();
Console.Error.WriteLine($"route={route} confidence={match.GetProperty("confidence").GetDouble():0.00} — {match.GetProperty("reason").GetString()}");

switch (route)
{
    case "deterministic":
    {
        // 2. Call the template tool directly with the extracted parameters — still no model.
        var tool = match.GetProperty("tool").GetString()!;
        var arguments = match.GetProperty("parameters").EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)p.Value.GetString());
        arguments["dryRun"] = dryRun;

        var result = await client.CallToolAsync(tool, arguments);
        Console.WriteLine(Text(result));
        return result.IsError == true ? 1 : 0;
    }

    case "model":
    {
        // 3. Not crystallized: this is where the app would send the prompt + ListTools result to Claude
        //    (Messages API with tools, or `claude -p`), plus the best template's context resource.
        var tools = await client.ListToolsAsync();
        Console.WriteLine("Escalate to Claude with:");
        Console.WriteLine($"  prompt:  {prompt}");
        Console.WriteLine($"  tools:   {string.Join(", ", tools.Select(t => t.Name))}");
        if (match.TryGetProperty("contextResource", out var ctx) && ctx.ValueKind == JsonValueKind.String)
            Console.WriteLine($"  context: {ctx.GetString()}");
        return 3;
    }

    default:
        Console.WriteLine("Routed to a human by policy: " + match.GetProperty("reason").GetString());
        return 4;
}

static JsonElement Json(CallToolResult result) => JsonDocument.Parse(Text(result)).RootElement;

static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
