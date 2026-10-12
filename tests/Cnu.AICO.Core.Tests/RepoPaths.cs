namespace Cnu.AICO.Core.Tests;

static class RepoPaths
{
    public static readonly string Root = FindRoot();
    public static string Templates => Path.Combine(Root, "templates");
    public static string Policy => Path.Combine(Root, "policy.yaml");
    public static string Golden => Path.Combine(Root, "tests", "golden");

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cnu.AICO.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Cnu.AICO.slnx) not found.");
    }

    /// <summary>Copies the templates folder so tests that write provenance do not touch the repo.</summary>
    public static string CopyTemplates()
    {
        var target = Path.Combine(Path.GetTempPath(), "aico-tests", Guid.NewGuid().ToString("N"), "templates");
        foreach (var file in Directory.EnumerateFiles(Templates, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(Templates, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        return target;
    }

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aico-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
