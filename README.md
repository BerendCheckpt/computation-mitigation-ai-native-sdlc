# Cnu.AICO — AI Computation Optimization for the AI-native SDLC

Reference implementation for running the **AI-native SDLC on GitHub with Claude** while **not invoking models for work
that has already been crystallized**. Approved, proven solutions become executable template specs, served
deterministically through an MCP server. Models are reserved for ideation, design, requested feedback, diagnosis and
non-critical fixes.

| Read | For |
|---|---|
| [docs/ai-native-sdlc-github.md](docs/ai-native-sdlc-github.md) | GitHub setup, the six stages, measurement, rollout |
| [docs/computation-mitigation.md](docs/computation-mitigation.md) | Crystallization lifecycle, MCP server, model-free matching (BM25/TF-IDF) |
| [docs/template-spec.md](docs/template-spec.md) | Writing a template |
| [docs/adr/](docs/adr) | Decisions: deterministic-first via MCP, template-as-spec, optional router, model tiering |

## Quick start
```bash
dotnet build Cnu.AICO.slnx
```
```bash
dotnet test Cnu.AICO.slnx
```
```bash
dotnet run scripts/aico-ci.cs -- match "Add a minimal API endpoint for entity Order in project Shop.Api"
```
```bash
dotnet run scripts/aico-ci.cs -- generate minimal-api-endpoint --out ./out entity=Order project=Shop.Api
```
In Claude Code the `cnu-aico` MCP server is registered through `.mcp.json`. Ask for an endpoint and the session calls
`generate_minimal_api_endpoint` instead of writing the code.

Optional, zero-model path (ADR 0003, sample only):
```bash
dotnet run --project samples/RouterClient -- --dry-run "Add a minimal API endpoint for entity Order in project Shop.Api"
```

## Layout
| Path | What |
|---|---|
| `src/Cnu.AICO.Core` | Spec model, matcher, policy, generator, regen verifier, KPIs, anomaly bands |
| `src/Cnu.AICO.McpServer` | MCP server (stdio / `--http`) |
| `templates/` | Crystallized templates (worked example: `minimal-api-endpoint`; its provenance is **illustrative sample data**) |
| `scripts/` | Deterministic CI commands (`aico-ci.cs`), template self-test |
| `.claude/` | Settings, hooks, skills, subagents |
| `.github/` | CODEOWNERS, issue forms, workflows |
| `evals/`, `ops/`, `policy.yaml` | Routing evals, runbooks/bands, model-use policy |
| `samples/RouterClient` | Optional MCP-client router sample (not production) |
