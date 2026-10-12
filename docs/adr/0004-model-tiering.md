# ADR 0004 — Model tiering per stage

- **Status:** Accepted
- **Date:** 2026-10-12

## Context
When a model *is* needed, the cheapest model that does the job well should be used. Stages differ a lot in how much
reasoning they need.

## Decision
| Stage / job | Model | Why |
|---|---|---|
| Routing, regen-verify, detection, KPI, plan notification | **none** | deterministic |
| Template curation, triage summaries | `claude-haiku-5-5` | pattern spotting, short outputs |
| Intent drafting (Stage 1), PR review, diagnosis, non-critical fixes | `claude-sonnet-5-5` | strong reasoning at moderate cost |
| Requirements & design spec (Stage 2) | `claude-opus-5-5` | highest leverage: errors here multiply downstream |

Models are set explicitly in each workflow (`claude_args: --model …`) and subagent frontmatter, so changes are
reviewed. `CLAUDE.md` and skills are kept short and stable so prompt caching applies across CI runs.

## Consequences
- **+** Cost follows value; the most capable model is used once per feature, not per PR.
- **−** Model ids must be updated deliberately when new versions ship (one PR touching workflows + agents).
