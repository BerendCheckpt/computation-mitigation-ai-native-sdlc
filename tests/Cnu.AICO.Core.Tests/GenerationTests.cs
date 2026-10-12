using Cnu.AICO.Core.Generation;
using Cnu.AICO.Core.Specs;

namespace Cnu.AICO.Core.Tests;

public class GenerationTests
{
    static readonly TemplateRegistry Registry = TemplateRegistry.Load(RepoPaths.Templates);
    static TemplateSpec Spec => Registry.Find("minimal-api-endpoint")!.Spec;

    static readonly Dictionary<string, string> OrderParams = new() { ["entity"] = "Order", ["project"] = "Shop.Api" };

    [Fact]
    public void Output_matches_golden_files()
    {
        var result = TemplateGenerator.Generate(Spec, OrderParams);
        var golden = Path.Combine(RepoPaths.Golden, "minimal-api-endpoint");

        if (Environment.GetEnvironmentVariable("AICO_UPDATE_GOLDEN") == "1")
        {
            if (Directory.Exists(golden)) Directory.Delete(golden, recursive: true);
            TemplateGenerator.WriteTo(golden, result);
        }

        foreach (var file in result.Files)
        {
            var path = Path.Combine(golden, file.Path);
            Assert.True(File.Exists(path), $"Golden file missing: {file.Path} (run with AICO_UPDATE_GOLDEN=1 after an approved template change)");
            Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), file.Content);
        }
        Assert.Equal(5, result.Files.Count);
    }

    [Fact]
    public void Generation_is_deterministic()
    {
        var a = TemplateGenerator.Generate(Spec, OrderParams);
        var b = TemplateGenerator.Generate(Spec, OrderParams);
        Assert.Equal(a.Files.Select(f => f.Content), b.Files.Select(f => f.Content));
    }

    [Fact]
    public void Missing_parameters_are_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => TemplateGenerator.Generate(Spec, new Dictionary<string, string> { ["entity"] = "Order" }));
        Assert.Contains("project", ex.Message);
    }

    [Fact]
    public void Invalid_parameters_are_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            TemplateGenerator.Generate(Spec, new Dictionary<string, string> { ["entity"] = "Order", ["project"] = "shop api; rm -rf" }));
        Assert.Contains("does not match", ex.Message);
    }

    [Fact]
    public void Marker_roundtrips()
    {
        var file = TemplateGenerator.Generate(Spec, OrderParams).Files[0];
        var info = GeneratedMarker.TryRead(file.Content)!;

        Assert.Equal("minimal-api-endpoint", info.TemplateId);
        Assert.Equal(Spec.Version, info.Version);
        Assert.Equal("Order", info.Parameters["entity"]);
        Assert.Equal("/orders", info.Parameters["route"]);
    }

    [Fact]
    public void Suggested_integration_tests_carry_no_generated_marker()
    {
        var suggested = TemplateGenerator.Generate(Spec, OrderParams).Files.Single(f => f.Kind == GeneratedKind.IntegrationSuggested);
        Assert.Null(GeneratedMarker.TryRead(suggested.Content));
        Assert.StartsWith("// aico:suggested", suggested.Content);
    }

    [Fact]
    public void Refuses_to_overwrite_hand_written_files()
    {
        var root = RepoPaths.TempDir();
        var result = TemplateGenerator.Generate(Spec, OrderParams);
        var target = Path.Combine(root, result.Files[0].Path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "// hand-written");

        Assert.Throws<InvalidOperationException>(() => TemplateGenerator.WriteTo(root, result));
    }

    [Theory]
    [InlineData("bash", "cat > \"$ROOT/src/Shop.Api/Features/Orders/Order.cs\" <<'AICO_EOF'")]
    [InlineData("powershell", "param([string]$Root = '.')")]
    public void Exports_standalone_scripts(string shell, string expected)
    {
        var script = ScriptExporter.Export(TemplateGenerator.Generate(Spec, OrderParams), shell);
        Assert.Contains(expected, script);
        Assert.Contains("No model required", script);
    }
}

public class RegenVerifierTests
{
    static readonly TemplateRegistry Registry = TemplateRegistry.Load(RepoPaths.Templates);

    static (string Root, GenerationResult Result) Generate()
    {
        var root = RepoPaths.TempDir();
        var result = TemplateGenerator.Generate(Registry.Find("minimal-api-endpoint")!.Spec,
            new Dictionary<string, string> { ["entity"] = "Order", ["project"] = "Shop.Api" });
        TemplateGenerator.WriteTo(root, result);
        return (root, result);
    }

    [Fact]
    public void Untouched_generated_files_verify_and_count_as_template_only()
    {
        var (root, result) = Generate();
        var verified = result.Files.Where(f => f.Kind != GeneratedKind.IntegrationSuggested)
            .Select(f => RegenVerifier.Verify(Registry, root, f.Path)).ToList();

        Assert.All(verified, r => Assert.Equal(VerifyStatus.Verified, r.Status));
        Assert.True(RegenVerifier.IsTemplateOnly(verified));
    }

    [Fact]
    public void Hand_edited_generated_file_is_a_mismatch()
    {
        var (root, result) = Generate();
        var path = Path.Combine(root, result.Files[2].Path);
        File.AppendAllText(path, "// sneaky edit\n");

        var r = RegenVerifier.Verify(Registry, root, result.Files[2].Path);

        Assert.Equal(VerifyStatus.Mismatch, r.Status);
        Assert.True(RegenVerifier.HasFailures([r]));
    }

    [Fact]
    public void Files_without_marker_are_not_template_only()
    {
        var (root, result) = Generate();
        var results = new[]
        {
            RegenVerifier.Verify(Registry, root, result.Files[0].Path),
            RegenVerifier.Verify(Registry, root, result.Files.Single(f => f.Kind == GeneratedKind.IntegrationSuggested).Path),
        };

        Assert.Equal(VerifyStatus.NotGenerated, results[1].Status);
        Assert.False(RegenVerifier.IsTemplateOnly(results));
        Assert.False(RegenVerifier.HasFailures(results));
    }

    [Fact]
    public void Moved_generated_file_is_a_mismatch()
    {
        var (root, result) = Generate();
        var moved = "src/Elsewhere/Order.cs";
        Directory.CreateDirectory(Path.Combine(root, "src", "Elsewhere"));
        File.Move(Path.Combine(root, result.Files[0].Path), Path.Combine(root, moved));

        Assert.Equal(VerifyStatus.Mismatch, RegenVerifier.Verify(Registry, root, moved).Status);
    }
}
