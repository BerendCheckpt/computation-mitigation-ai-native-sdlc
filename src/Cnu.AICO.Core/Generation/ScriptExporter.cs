using System.Text;

namespace Cnu.AICO.Core.Generation;

/// <summary>
/// Turns a generation result into a stand-alone script, so a crystallized flow can run in a pipeline or
/// on an engineer's machine with no MCP server and no model. The script embeds the rendered files verbatim.
/// </summary>
public static class ScriptExporter
{
    public static string Export(GenerationResult result, string shell) => shell.ToLowerInvariant() switch
    {
        "bash" or "sh" => Bash(result),
        "powershell" or "pwsh" or "ps1" => PowerShell(result),
        _ => throw new ArgumentException($"Unsupported shell '{shell}'. Use bash or powershell."),
    };

    static string Bash(GenerationResult r)
    {
        var sb = new StringBuilder();
        sb.Append("#!/usr/bin/env bash\n");
        sb.Append($"# Deterministic flow exported from template {r.TemplateId}@{r.Version}. No model required.\n");
        sb.Append($"# Parameters: {string.Join(", ", r.Parameters.Select(kv => $"{kv.Key}={kv.Value}"))}\n");
        sb.Append("set -euo pipefail\n");
        sb.Append("ROOT=\"${1:-.}\"\n\n");
        foreach (var f in r.Files)
        {
            sb.Append($"mkdir -p \"$ROOT/{Path.GetDirectoryName(f.Path)!.Replace('\\', '/')}\"\n");
            sb.Append($"cat > \"$ROOT/{f.Path}\" <<'AICO_EOF'\n{f.Content.TrimEnd('\n')}\nAICO_EOF\n\n");
        }
        foreach (var a in r.PostActions)
            sb.Append($"(cd \"$ROOT\" && {a})\n");
        sb.Append($"echo \"Generated {r.Files.Count} file(s) from {r.TemplateId}@{r.Version}.\"\n");
        return sb.ToString();
    }

    static string PowerShell(GenerationResult r)
    {
        var sb = new StringBuilder();
        sb.Append($"# Deterministic flow exported from template {r.TemplateId}@{r.Version}. No model required.\n");
        sb.Append($"# Parameters: {string.Join(", ", r.Parameters.Select(kv => $"{kv.Key}={kv.Value}"))}\n");
        sb.Append("param([string]$Root = '.')\n$ErrorActionPreference = 'Stop'\n");
        sb.Append("$utf8 = New-Object System.Text.UTF8Encoding($false)\n\n");
        foreach (var f in r.Files)
        {
            var target = $"(Join-Path $Root '{f.Path.Replace("'", "''")}')";
            sb.Append($"$p = {target}\n");
            sb.Append("New-Item -ItemType Directory -Force -Path (Split-Path $p) | Out-Null\n");
            sb.Append($"[System.IO.File]::WriteAllText($p, (@'\n{f.Content.TrimEnd('\n')}\n'@ -replace \"`r`n\", \"`n\") + \"`n\", $utf8)\n\n");
        }
        foreach (var a in r.PostActions)
            sb.Append($"Push-Location $Root; try {{ {a} }} finally {{ Pop-Location }}\n");
        sb.Append($"Write-Host 'Generated {r.Files.Count} file(s) from {r.TemplateId}@{r.Version}.'\n");
        return sb.ToString();
    }
}
