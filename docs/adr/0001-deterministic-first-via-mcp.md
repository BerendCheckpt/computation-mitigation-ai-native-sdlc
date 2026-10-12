# ADR 0001 — Deterministic first, served through an MCP server

- **Status:** Accepted
- **Date:** 2026-10-12
- **Deciders:** tech lead, platform team

## Context
Model invocation costs money, latency and energy. Result caching (as offered by vendors seen at World Summit AI)
still sends every prompt to a model. In our SDLC many requests repeat approved, proven solutions (endpoints, mappings,
plugins, tests). For those, a model adds no value beyond choosing parameters — and adds non-determinism, which the
AI-native SDLC playbook explicitly asks us to move into a deterministic layer.

## Decision
1. Approved, proven solutions are **crystallized** into templates: executable specs (`spec.yaml` + Scriban content +
   shipped unit tests + suggested integration tests + context + provenance). Lifecycle and gates: `docs/computation-mitigation.md`.
2. Templates are served by **`Cnu.AICO.McpServer`** (official `ModelContextProtocol` C# SDK, .NET 10):
   `match_template` (deterministic routing), one `generate_<id>` tool per crystallized template (input schema =
   template parameters), `export_script`, `record_outcome`, and context resources.
3. `ListTools` advertises **only crystallized** templates; policy (`policy.yaml`) is enforced in the server.
4. Every surface uses the same server: Claude Code sessions (`.mcp.json` + `UserPromptSubmit` hook), CI workflows
   (`--mcp-config`), and — optionally — a router (ADR 0003).

## Consequences
- **+** The expensive part — the model *writing* the code (output tokens) — disappears for crystallized work; the model
  emits a ~50-token tool call. Fewer turns, fewer build-fix loops, reproducible output.
- **+** Template-only PRs are verified by regenerate-and-diff, so AI review is skipped for them.
- **+** One integration point for every client; transport-agnostic (stdio locally, HTTP for shared use).
- **−** A model call still happens to *select* the tool in interactive sessions (mitigated by the hook; removed entirely
  only by the optional router).
- **−** Templates need owners, versioning and promotion discipline (CODEOWNERS, validator, golden files).

## Alternatives considered
- **Prompt/result cache only** — still invokes the model; no reproducibility.
- **Skills only** — advisory; the model still writes the code token by token.
- **`dotnet new` templates** — good for project scaffolding, but no routing, no provenance, no MCP schema; could be a
  future output format of the same specs.
