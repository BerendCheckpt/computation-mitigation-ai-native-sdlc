# Spec 0001: Local-first SDLC: agents in Docker Sandboxes, no API key

Status: approved by the product owner and tech lead in session (2026-10-15). Plan: [plan.md](plan.md).

## Problem
Claude Code runs only on the engineer's own machine, signed in with the Claude subscription. The GitHub workflows that call Claude need `ANTHROPIC_API_KEY`, which engineers do not have and the repository never had, so they cannot run. Agents also need an isolated place to run that does not share the host kernel. Engineers need one overview of issues, running agents and to-do's.

## Requirements
R1. **No API key.** No workflow, script or doc requires `ANTHROPIC_API_KEY`. Claude Code signs in with the Claude subscription (`/login`).

R2. **Model work runs locally.** Workflows on GitHub stay deterministic: build, test, gates, notify, detect and report.

R3. **Agents run in microVMs.** Every agent runs in its own Docker Sandbox (`sbx`), with its own kernel. Its sub-agents run in the same sandbox. The engineer installs only `sbx`, plus git and the hub executable. Anthropic's devcontainer is not used.

R4. **Isolation.** Each sandbox meets these conditions:
- clone mode, with the host repository mounted read-only;
- network denied by default, with an explicit allowlist;
- credentials injected by the host proxy, so no raw secrets enter the VM;
- no bypass-permissions mode;
- access to the host only on the hub port and the SonarQube port.

R5. **Hub.** The hub runs on the host, never in a VM. It is a local page opened as a tab in the Claude desktop browser pane, and it shows:
- the repository's open issues, each with a button that starts an agent in plan mode (no button for critical issues);
- the running agents with their sub-agents;
- to-do's: the agents' task lists, "waiting on me", and a personal to-do list that stays on the host.

R6. **SonarQube.** A skill scans for bloat, conventions and bugs, without security rules. It runs only after code is written and is never part of a workflow. The server comes from `~/Documents/Tools/Sonarqube` (overridable with `SONARQUBE_HOME`), running in its own sandbox.

R7. **Token ledger.** Every agent session records its token usage as a ledger fragment, which is committed with the PR. A run that invokes no model validly records 0 tokens. A result is unreliable only when a model was invoked and the totals are zero or the run crashed.

R8. **Unchanged.**
- The issue and label workflow.
- `critical` routes to a human.
- A single maintainer can merge on their own.

R9. **Minimal.** Prefer extending existing files. Every new file is justified in plan.md.

## Terminology
See `## Terminology` in `CLAUDE.md`.

## Out of scope
- Egress firewall tuning beyond the kit allowlist.
- Org-managed sandbox policies.
- Cloud sandboxes.
- Script conversion.
