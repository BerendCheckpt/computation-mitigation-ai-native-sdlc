using Cnu.AICO.McpServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Cnu.AICO MCP server — serves crystallized templates as deterministic tools.
//   stdio (default):  dotnet run --project src/Cnu.AICO.McpServer
//   HTTP (CI/shared): dotnet run --project src/Cnu.AICO.McpServer -- --http http://localhost:5100
var options = AicoOptions.From(args);

if (options.HttpUrl is null)
{
    var builder = Host.CreateApplicationBuilder(args);
    // stdout carries the MCP protocol; all logging must go to stderr.
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddAico(options).WithStdioServerTransport();
    await builder.Build().RunAsync();
}
else
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Services.AddAico(options).WithHttpTransport();
    var app = builder.Build();
    app.MapMcp("/mcp");
    await app.RunAsync(options.HttpUrl);
}
