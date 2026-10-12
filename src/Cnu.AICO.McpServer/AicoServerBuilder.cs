using Cnu.AICO.Core.Specs;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Cnu.AICO.McpServer;

public static class AicoServerBuilder
{
    public const string Instructions =
        "Deterministic-first code generation. Before writing code for a request, call match_template. " +
        "If route=deterministic, call the returned generate_* tool with its parameters instead of writing the code. " +
        "If route=model, implement normally and read the contextResource if given. If route=human, stop and explain.";

    /// <summary>Registers the shared context, the static tools, one generate tool per crystallized template, and resources.</summary>
    public static IMcpServerBuilder AddAico(this IServiceCollection services, AicoOptions options)
    {
        var aico = new AicoContext(options);
        services.AddSingleton(aico);

        return services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "cnu-aico", Version = typeof(AicoServerBuilder).Assembly.GetName().Version?.ToString() ?? "1.0.0" };
                o.ServerInstructions = Instructions;
            })
            .WithTools<AicoTools>()
            // ListTools advertises only crystallized templates: unapproved flows are invisible to the model.
            .WithTools(aico.Registry.Crystallized.Select(e => (McpServerTool)new GenerateTemplateTool(aico, e.Spec)).ToList())
            .WithResources(BuildResources(aico.Registry));
    }

    static IEnumerable<McpServerResource> BuildResources(TemplateRegistry registry)
    {
        foreach (var entry in registry.Crystallized)
        {
            var spec = entry.Spec;
            yield return McpServerResource.Create(
                () => TemplateRegistry.ReadContext(spec),
                new McpServerResourceCreateOptions
                {
                    UriTemplate = $"aico://templates/{spec.Id}/context.md",
                    Name = $"{spec.Id}-context",
                    Description = $"Why/when (not) to use {spec.Id}.",
                    MimeType = "text/markdown",
                });
            yield return McpServerResource.Create(
                () => File.ReadAllText(Path.Combine(spec.Directory, TemplateRegistry.SpecFileName)),
                new McpServerResourceCreateOptions
                {
                    UriTemplate = $"aico://templates/{spec.Id}/spec.yaml",
                    Name = $"{spec.Id}-spec",
                    Description = $"Executable spec of {spec.Id}: parameters, requirements, outputs.",
                    MimeType = "application/yaml",
                });
        }
    }
}
