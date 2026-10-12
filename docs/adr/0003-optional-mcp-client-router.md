# ADR 0003 — Deterministic front-door router (optional; recommended, not built)

- **Status:** Proposed — documented as a suggestion; the organisation has no plan to build it now
- **Date:** 2026-10-12

## Context
Claude Academy, *Introduction to MCP*, lesson 2 ("MCP clients"): the application sits in front of the MCP client.
It asks the client for tools (`ListToolsRequest/Result`), sends the user query *plus the tool list* to Claude, and
executes whatever tool Claude picks (`CallToolRequest/Result`). **The application decides whether Claude is involved.**

With ADR 0001, a crystallized request still costs one model round trip in which Claude does nothing but select
`generate_<id>` and copy parameters — and every such request also carries the full tool definitions as input tokens.

## Proposal
A thin router acting as an **MCP client**:
1. receives the prompt (CLI, webhook, chat bot, CI step);
2. calls `match_template` over MCP — deterministic (BM25 + slot extraction + policy; see appendix A1 of `docs/computation-mitigation.md`);
3. on a confident match, sends `CallToolRequest generate_<id>` **directly** → **zero model tokens**;
4. otherwise forwards the prompt to Claude with the `ListTools` result and the best candidate's context resource.

If built: **.NET 10 Native AOT**, not C++ — one toolchain, ~10–30 ms startup, small binary, reuses the same server
and specs. Routing costs microseconds next to any model call, so C++ would buy nothing measurable.

## Why it is worth it (efficiency and cost)
- It removes the *last* model call on the crystallized path, including the tool-list input tokens sent with every
  request. Per request the saving is small; **at volume** (CI bots, monitoring agents, retailer-onboarding batches,
  chat front-ends) it is the remaining cost and energy line.
- Latency drops from seconds (model round trip) to milliseconds.
- Fully reproducible: no sampling anywhere on the path.

## Why not now
- The MCP server alone already captures most of the savings: the expensive part is the model **writing** the code
  (output tokens, multiple turns, build-fix loops), and that is gone either way.
- Interactive engineers work in Claude Code anyway, where the `UserPromptSubmit` hook already steers to the template.
- Building it means another deployable to operate. Re-evaluate when the KPI report shows high automated volume of
  `match_template → deterministic` decisions.

## Evidence
`samples/RouterClient` (~100 lines, demo — not production) connects to `Cnu.AICO.McpServer` over stdio, calls
`match_template`, and on a deterministic route calls `generate_*` directly. It produces the same files as a Claude
session with no model involved. It is also the starting point if the organisation changes its mind.
