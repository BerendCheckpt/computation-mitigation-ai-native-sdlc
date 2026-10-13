# CLAUDE.md — Cnu.AICO reference implementation

AI-native SDLC on GitHub with a deterministic layer: crystallized, human-approved templates are served through the
`cnu-aico` MCP server so models *select and parameterise* instead of *generate*. Docs: `docs/`.

## Deterministic first (most important rule)
- Before writing code for a request, call `match_template` (MCP `cnu-aico`). If `route=deterministic`, call the returned
  `generate_*` tool with its parameters instead of writing the code. If `route=human`, stop and explain.
- Never hand-edit a file whose first line contains `aico:generated` — regenerate it, or remove the marker deliberately.
- If you implement the same pattern a third time, propose a template with the `crystallize-template` skill.

## Terminology
- 'Wiki' means [specify: the GitHub Wiki repo (<repo>.wiki.git) / the docs site / a /wiki folder]. If a term like this is ambiguous, ask with AskUserQuestion before building anything.

## Architecture
- `src/Cnu.AICO.Core` — spec model, matcher (normalizer, BM25, TF-IDF, slots), policy, generator (Scriban), regen verifier, KPIs, anomaly bands.
- `src/Cnu.AICO.McpServer` — MCP server (stdio default, `--http <url>`); tools `match_template`, `generate_<id>`, `export_script`, `record_outcome`; resources `aico://templates/<id>/…`.
- `templates/<id>/` — `spec.yaml` (contract), `context.md`, `content/`, `tests/unit/`, `tests/integration.suggested/`, `provenance.json`.
- `scripts/aico-ci.cs`, `.claude/hooks/aico-hook.cs` — .NET 10 file-based scripts used by CI and hooks.
- `samples/RouterClient` — OPTIONAL sample (ADR 0003), not production.

## Terminology
- 'Wiki' means [specify: the GitHub Wiki repo (<repo>.wiki.git) / the docs site / a /wiki folder]. If a term like this is ambiguous, ask with AskUserQuestion before building anything.

## CI Safety
- After editing .gitignore, workflow YAML, or anything in .github/, run `git status --ignored` and `actionlint` (if available) and confirm that the files CI needs are still tracked, before committing.
- Make file edits with the Edit tool, not sed/awk/heredoc scripts. Scripted edits have mangled lines before.

## Commands
| What | Command | Passing looks like |
|---|---|---|
| Build | `dotnet build Cnu.AICO.slnx` | `Build succeeded.` 0 warnings (warnings are errors) |
| Test | `dotnet test Cnu.AICO.slnx` | `Passed!` for both test projects |
| Validate templates | `dotnet run scripts/aico-ci.cs -- validate` | `0 error(s)` |
| Update golden files (after an approved template change only) | `AICO_UPDATE_GOLDEN=1 dotnet test tests/Cnu.AICO.Core.Tests` | then review the diff in `tests/golden/` |
| Run MCP server | `dotnet run --project src/Cnu.AICO.McpServer` | waits on stdio |
| Diagnose the latest failed CI run (headless; CI does this automatically in `maintain-detect`) | `claude -p "Run 'gh run view --log-failed' for the latest failed run on this branch, diagnose the cause, and propose a minimal fix" --allowedTools "Bash(gh run view:*),Read,Edit" --output-format json` | a root cause and a minimal fix; the JSON `result` holds the answer and its token usage |

## Conventions
- .NET 10, nullable on, file-scoped namespaces, `sealed` by default, records for data, no reflection-heavy magic in Core.
- Template output must be deterministic: same version + same params ⇒ identical bytes (LF line endings).
- Bump `version` in `spec.yaml` for any template content change; regenerate golden files in the same PR.

## Definition of done
Build + tests pass, `validate` passes, golden diff reviewed, docs updated when behaviour changes, no new warnings.

## Known mistakes (add a line when something happens twice)
- `dotnet run file.cs` defaults to AOT-style trimming: keep `#:property PublishAot=false` in scripts that use Core.
- Golden files live in `tests/golden/`, never inside a test project (they would be compiled).
- Never put `NaN`/`Infinity` in anything that is serialized to JSON (it throws): use `double?` and `null` for "unknown" (issue #4).
