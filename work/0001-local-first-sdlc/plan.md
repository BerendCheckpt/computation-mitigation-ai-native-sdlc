# Plan: Local-first SDLC in Docker Sandboxes: one microVM per agent, no API key

## Context
The organisation's SDLC runs Claude Code **only locally**, signed in with the Claude subscription (`/login`), never `ANTHROPIC_API_KEY`. Git, issues, labels, workflows and quality gates stay on GitHub.

A container shares the host kernel, which is too high a risk for coding agents. Agents therefore run in **Docker Sandboxes** (`sbx`).
- Each sandbox is a **microVM with its own kernel**, its own private Docker daemon, and an outbound proxy that blocks everything not explicitly allowed.
- Credentials are injected by that proxy on the host and never enter the VM.
- `sbx` needs no Docker Desktop and no Docker Engine. **Anthropic's devcontainer is not needed and is removed from the plan:** no `.devcontainer/`, no compose file, no Dockerfile.

Seven workflow jobs that call `claude-code-action` go away; the API key was never set, so they never ran. Two things are added:
- a **local hub** for issues, agents and sub-agents, with a "Plan" button per issue;
- a **SonarQube skill** for bloat, conventions and bugs, local only and run after code is written.

Rules for this change:
- **Minimal first.** Prefer extending existing files, and nothing extra unless an engineer asks.
- **Issue and label workflow unchanged.**

State checked (2026-10-15):
- `main` = `ef3dd82`. The ruleset needs 0 approvals and gives admins a bypass, so you can merge on your own.
- Not on this machine: `sbx`, Docker, Java, `jq`, `actionlint`, `dotnet-sonarscanner`, `claude` CLI.
- `~/Documents/Tools/Sonarqube` is the SonarQube Community 26.10 server, with Linux JREs bundled under `jres/`.
- There is no `work/` folder.

Two questions went unanswered, so the plan uses the recommended defaults. Both are marked **[default]** below:
- the hub is a self-contained executable;
- the host has git plus a checkout of the repo.

## Terminology (answers → `## Terminology` in CLAUDE.md, replacing the placeholder)
- **Wiki** = the GitHub Wiki repo (`<repo>.wiki.git`).
- **Baseline** = only the *token baseline*: the tokens of the previous model run for the same prompt or template. The ledger sets it automatically, and there is never a separate action. Sonar's equivalent is called the "new-code period".
- **Critical** = the `critical` label (customer-facing, data-affecting or security) **or** a SonarQube Blocker/Critical finding (Blocker/High in its newer scheme). Either way:
  - the work goes to a human, with no autonomous fix;
  - the hub shows no Plan button;
  - the PR gets the `critical` label.
- **Agent** = one Claude Code session in its own sandbox. **Sub-agent** = a sub-agent started inside that session; it shares the agent's microVM.
- **Sandbox** = a Docker Sandboxes microVM (`sbx`), named `aico-issue-N` for an issue agent or `aico-sonarqube` for the Sonar server.
- **Hub** = a local overview page served by `aico-hub` on the **host** (never in a VM). It shows the repository's issues, the running agents, and to-do's, and it opens as a tab in the Claude desktop app's browser pane, next to the Code tab.
- **To-do's** = three lists:
  - each agent's own task list, live;
  - "Waiting on me": agents waiting for a permission or input, plans waiting for approval, and open PRs from `issue-*` branches;
  - the engineer's personal to-do list.

## Architecture
```
host: sbx (+ git, repo checkout [default]) — no Docker Desktop, no .NET
 ├─ aico-hub (self-contained exe [default], http://127.0.0.1:5171) ── calls `sbx` on the host
 ├─ sandbox aico-issue-N   one per Plan click · --clone (private clone; host repo read-only)
 │     claude (plan mode) + its sub-agents · .NET 10, gh, jq, actionlint, sonarscanner
 │     hooks → POST http://host.docker.internal:5171/events
 │     Sonar  → http://host.docker.internal:9000
 └─ sandbox aico-sonarqube  shell agent · mounts ~/Documents/Tools/Sonarqube · publishes 9000
```
- **Credentials:**
  - Claude: `/login` with the subscription. The OAuth runs on the host, and the proxy injects the token.
  - GitHub: `sbx secret set github`, a fine-grained PAT for this repo only, entered once interactively. `gh` and `git push` inside the VM work through the proxy, and the token never enters the VM.
  - SonarQube: `sbx secret set sonar` (a custom secret).
- **Network:**
  - deny by default;
  - the kit allows the Anthropic, GitHub and NuGet/.NET hosts;
  - once, globally: `sbx policy allow network localhost:5171` and `localhost:9000`, so agents can reach the hub and SonarQube.
  - Agents have no other route to the host, and no access to the host's Docker or files apart from the read-only source.
- **Permissions inside the VM:** the `claude` workload starts with `--dangerously-skip-permissions` by default. Project settings add `permissions.disableBypassPermissionsMode: "disable"`, so agents always run in plan mode or with normal prompts. Whether bypass mode is really refused in a sandbox, so plan mode starts, is verified at implementation.

**One-time setup per engineer** (in `docs/ai-native-sdlc-github.md` Part A):
1. `winget install -h Docker.sbx`, enable Windows Hypervisor Platform, then `sbx login`. This needs a Docker account.
2. `sbx secret set github`, `sbx secret set sonar`, and the two `sbx policy allow network` lines.
3. Download `aico-hub` from the latest GitHub Release and run it.
4. Start SonarQube from the hub ("Start SonarQube"); then change the admin password and create a token.
5. On the first Plan, `/login` in Claude once.

## Change list: every file, with justification
**New files (12)**
| File | Why it can't be an extension |
|---|---|
| `work/0001-local-first-sdlc/spec.md`, `plan.md` | The requirements you named; nothing exists yet |
| `sandbox/aico-dotnet/aico-dotnet.yaml` + `aico-dotnet.dockerfile` | A v3 **mixin** kit: the .NET 10 SDK, a pinned `actionlint`, and `dotnet-sonarscanner` on top of the Claude workload (which already has git, gh, jq and node). Its runtime network policy covers `api.nuget.org`, `github.com`, `api.github.com` and the SonarQube port `host.docker.internal:9000` (the SDK is downloaded at kit build, so no .NET download host at runtime), and it declares the proxy-injected `github` and `sonar` credentials. `sbx` builds a local kit directory itself, so no registry is needed |
| `sbxenv.yaml` (repo root) | The sandbox definition: `schemaVersion: "1"`, the v3 Claude workload `docker.io/docker/sbx-kit-claude:<pinned>` plus `./sandbox/aico-dotnet`, `workspace: {path: ., clone: true}`, and no `lifecycle` or `command` secrets. Clone mode mounts the repo read-only, so an agent can't rewrite this file (the safety rule in the docs) |
| `scripts/aico-hub.cs` | The hub: a local server that never runs in CI; BCL `HttpListener`, no packages. CI publishes it as a Native AOT single-file executable [default] |
| `src/Cnu.AICO.Core/Ops/LocalHub.cs` + `tests/…/LocalHubTests.cs` | Testable hub logic: fold hook events into agents and sub-agents; policy check per issue (`PolicyEvaluator`); prompt and `sbx` argument list |
| `src/Cnu.AICO.Core/Ops/SonarFindings.cs` | Testable classification for `aico-ci sonar` (bloat, conventions, bugs; Blocker/High = critical), kept out of the script like the other Core logic |
| `.claude/launch.json` | A 6-line URL-only entry, so Claude desktop's preview opens the running hub in its browser pane; no existing file holds launch entries |
| `.claude/skills/sonarqube/SKILL.md` | The requested skill |
| `.github/workflows/hub-release.yml` | Builds `aico-hub` (Native AOT for win-x64, osx-arm64 and linux-x64) on a `hub-v*` tag and attaches it to a GitHub Release. This is the only way to give the host a hub without installing .NET. No model is involved, and it needs only `contents: write` |

**Deleted, because they need an API key** (B):
- `stage-1-intent.yml`
- `stage-6-agent-fix.yml`
- `claude-on-demand.yml`
- `claude-review.yml`
- `.github/actions/aico-record-usage/`

**Extended**
- **A: Core, scripts and tests**
  - `ClaudeRunResult.cs`: `Parse` also accepts a Claude Code session transcript. It sums `message.usage` of assistant entries, deduplicated by `message.id`, plus sub-agent transcripts.
  - `TotalsUnreliable` is true **only when a model was invoked** (assistant messages, `modelUsage` or `num_turns > 0`) and the totals are zero or the run crashed. A run without a model invocation validly records 0 tokens. Tests for both cases go in `TokenLedgerTests.cs`.
  - `aico-ci.cs`:
    - `ledger record` accepts repeated `--result` files;
    - new `sonar` command. It uses `HttpClient` against `api/issues/search` for the whole project.
      - Scope: Maintainability and Reliability only; no security issues or hotspots.
      - Output: markdown grouped into bloat (size, complexity, duplication), conventions and bugs.
      - Exit code 2 on a Blocker/High finding.
- **B: workflow drafts**
  - `stage-2-spec.yml`: no-model notify. On a merged `intent.md` it opens a `stage:spec` issue, like `stage-3-plan-notify`.
  - `template-verify.yml`: drop the `review` job. The summary notes that AI review runs locally unless the PR is template-only.
  - `maintain-detect.yml`: drop the three Claude jobs and the `workflow_run` trigger. From 2σ up, `detect` files the anomaly issue itself (`stage:maintain`). At 3σ it adds `agent-fix-allowed` for non-critical metrics.
  - `kpi-report.yml`: `ledger merge --from ledger/fragments`, then `git rm` the merged fragments in the kpi PR.
  - `ci.yml`:
    - one job builds the kit (`docker buildx build -f sandbox/aico-dotnet/aico-dotnet.yaml sandbox/aico-dotnet`). Nothing else checks it, and GitHub runners have buildx;
    - one job runs a Native AOT publish of the hub, so a broken AOT build shows up before a release.
- **C: governance drafts**
  - `.claude/settings.json`:
    - `permissions.disableBypassPermissionsMode: "disable"`;
    - `SessionStart`, `SessionEnd`, `SubagentStart` and `SubagentStop` call `aico-hook.cs -- track`;
    - the `jq`/`actionlint` PostToolUse command becomes `aico-hook.cs -- lint`.
  - `.claude/hooks/aico-hook.cs`:
    - `track` POSTs the event to `$AICO_HUB_URL/events`: sandbox, session, issue, branch, and the sub-agent type and id. If the hub isn't reachable, it skips silently.
    - On `SessionEnd`, `track` also writes `ledger/fragments/<session>.jsonl` through `aico-ci ledger record`, and that fragment is committed with the PR.
    - `lint` does in .NET what the `jq` hook did.
  - `policy.yaml`: comments only. Model contexts run in local sandboxes, and a Sonar Blocker/High counts as `critical`. Rules are unchanged.
  - `ops/bands.md`: the Diagnose and Act rows become "issue filed (no model) → engineer starts an agent from the hub".
  - `protected-paths.txt`: add `sbxenv.yaml` and `sandbox/**`, because they define the agent's isolation.
  - CODEOWNERS: add `/sbxenv.yaml` and `/sandbox/` → `@cnu/tech-leads @cnu/security`.
  - Labels: no change.
- **Main session: docs**
  - `CLAUDE.md`:
    - Terminology.
    - Commands: rows for the hub and for `aico-ci sonar`. The headless `claude -p` row no longer says "CI does this".
    - Definition of done: "Sonar has no Blocker/High findings".
  - `docs/ai-native-sdlc-github.md`: Part A gets the "Engineer machine" setup above; the Part B rows are updated.
  - `docs/computation-mitigation.md`: its API-key mention.

## Hub (`aico-hub`, http://127.0.0.1:5171): host only, opened in the Claude desktop browser pane
- **Opening it:** `.claude/launch.json` (a new, 6-line file) gets a URL-only entry `aico-hub` → `http://127.0.0.1:5171`. Claude Code's preview can then open the hub as a tab in the browser pane next to the Code tab. The engineer starts the exe once; the launch entry only attaches to it.
- **Layout, three columns:**
  - **Issues:** open repository issues with labels, stage and assignee, plus the Plan button.
  - **Agents:** running sandboxes with their sessions and nested sub-agents.
  - **To-do's:**
    - **Agent task lists:** a PostToolUse hook on `TodoWrite|TaskCreate|TaskUpdate` forwards the agent's current list, shown per agent and sub-agent.
    - **Waiting on me:** from the `Notification` hook (permission or input needed, plan waiting for approval), plus open PRs from `issue-*` branches through the GitHub REST API.
    - **Personal:** add, check and delete items, stored only on the host in `%LOCALAPPDATA%/aico-hub/todos.json` (on macOS/Linux, `~/.local/share/aico-hub`). Never committed.
- C's `settings.json` draft therefore also adds the `Notification` hook and the PostToolUse task-list matcher, both calling `aico-hook.cs -- track`.
- **Page:** one embedded HTML page that polls `/api/state` every 5 seconds. Every POST needs a per-launch CSRF token and passes an Origin check.
- **Event intake:** `/events` accepts hook events only from sandboxes, which reach it through `host.docker.internal`.
- **Issue cards:** issues are read through the GitHub REST API with the engineer's PAT, so there is no `gh` on the host. Sandbox status comes from `sbx ls`.
- **Plan button:**
  - First it re-reads the issue from GitHub, so a `critical` label added since the last refresh still blocks.
  - Then, from the repo checkout, it runs two commands:
    1. `sbx env create --force --name aico-issue-N --env-arg issue=N <checkout>`, which creates the sandbox from `sbxenv.yaml` (clone mode, kits, `AICO_ISSUE`, `AICO_HUB_URL`) without attaching. `sbx env` takes no agent arguments.
    2. `sbx run --detach --name aico-issue-N -- "<prompt>" --permission-mode plan`. The prompt comes first: a first argument that is not a flag replaces sbx's default `--dangerously-skip-permissions`, so Claude starts in plan mode. `disableBypassPermissionsMode` in the project settings is the safety net.
  - The prompt holds only the issue number and a fixed hint per stage label:
    - `stage:intent` → write-intent
    - `stage:spec` → write-spec
    - `stage:plan` → plan.md
    - `agent-fix-allowed` → failing test first
    - `stage:maintain` → read-only diagnosis with the incident-diagnoser agent, written as an issue comment

    It also tells the agent to work on branch `issue-N`. The issue text is read inside the VM with `gh issue view`, as data.
  - Arguments are passed through `ArgumentList`.
  - Issues labelled `critical` get no button.
- **Card actions:**
  - "Attach": copies `sbx run --name aico-issue-N`, which reconnects to the running agent.
  - "Fetch branch": copies `git fetch sandbox-aico-issue-N`.
  - "Stop": runs `sbx stop`.
- **Agents panel:** each sandbox's sessions (running or ended, issue, branch, tokens from the fragment) with their sub-agents nested (type, running or done, duration).
- **Start SonarQube:** creates the `aico-sonarqube` sandbox if it is missing. It is idempotent.

## SonarQube
- **Server:** sandbox `aico-sonarqube`.
  - Shell agent, mountless, with `additionalWorkspaces` `${SONARQUBE_HOME:-~/Documents/Tools/Sonarqube}` (read-write). That is a relative path, so it works on any machine with SonarQube at that location.
  - Runs `bin/linux-x86-64/sonar.sh console` with the Linux JRE from the folder's own `jres/` (no Java install) and `-Dnode.store.allow_mmap=false`.
  - Publishes port 9000 on host loopback. Its data stays in your folder.
- **Skill** (`allowed-tools`: `dotnet sonarscanner`, `dotnet build`, `dotnet run scripts/aico-ci.cs`):
  - **When:** only after code is written and before a PR; never in a workflow.
  - **Run:**
    1. `dotnet sonarscanner begin /k:cnu-aico /d:sonar.host.url=http://host.docker.internal:9000`; the token comes from the injected `sonar` secret.
    2. `dotnet build --no-incremental`.
    3. `dotnet sonarscanner end`.
    4. `dotnet run scripts/aico-ci.cs -- sonar`.
  - **Afterwards:**
    - The skill reports the grouped findings.
    - Fixes are made only when the engineer asks.
    - Exit code 2 means: stop, and the PR gets the `critical` label.

## Execution
This build runs on the current host, because sandboxes aren't set up until this change lands.
1. Branch `feat/local-first-sdlc` from `origin/main`. Commit `work/0001…/spec.md`, `plan.md` and the CLAUDE.md Terminology.
2. Parallel sub-agents:
   - **A** (`isolation: worktree`): Core, tests, `aico-ci.cs`, `aico-hub.cs`, the `sandbox/` kit and `sbxenv.yaml`.
     - It calls `match_template` first.
     - Failing tests come first.
     - No golden files change.
   - **B**: drafts the workflows in the scratchpad.
   - **C**: drafts the settings, hook, policy, bands, protected paths and CODEOWNERS in the scratchpad, and applies nothing.
   - **D** (read-only auditor, after A, B and C), line by line:
     - every label, workflow, secret, path, kit reference and network-policy entry that is referenced exists, and no workflow still references `ANTHROPIC_API_KEY`;
     - no reusable workflow asks for more than its caller grants;
     - no fork runs with secrets;
     - every Claude session records usage;
     - no new baseline or measurement workflow;
     - `critical` routes to a human;
     - you can still merge on your own;
     - agents run in a microVM in clone mode, with no bypass mode, no raw credentials in the VM, no host access except the two allowed ports, and an `sbxenv.yaml` they can't rewrite.

     D also runs `dotnet build`, `dotnet test`, `aico-ci validate` and a local AOT publish of the hub.
3. Merge only after D reports no blocking findings.
4. Show you D's report and the exact workflow and governance diffs. Then commit the protected files through the GitHub contents API on the branch.
5. Self-verify loop:
   - Open a **draft PR**. It triggers `ci` (including the kit and hub builds) and `template-verify`; watch them with `gh run watch`.
   - `kpi-report`, `maintain-detect`, `deploy` and `hub-release` (tag-only) are not run.
   - At most 3 reruns; check `.gitignore`, paths, permissions and YAML first.
   - When green, `gh pr ready`. The description lists the changes, each CI failure and its fix, and a link to the green run.
   - If still red: stop and give a diagnosis. Never weaken protection or skip checks. Never merge.

## Verification
- **Build and tests:** `dotnet build` reports 0 warnings, `dotnet test` passes (parser, `TotalsUnreliable` and `LocalHub`), `validate` reports 0 errors, and the CI kit build and hub AOT build are green.
- **Sandboxes** (needs `sbx` and Windows Hypervisor Platform on your machine):
  - The hub lists open issues, and a `critical` issue has no button.
  - Plan → `sbx ls` shows `aico-issue-N`, and attaching shows Claude in **plan mode**, not bypass.
  - A sub-agent appears in the Agents panel.
  - Inside the VM, `curl` to a host not on the allowlist is blocked.
  - `SessionEnd` writes a fragment with non-zero tokens, and `git fetch sandbox-aico-issue-N` brings the branch to the host.
- **Sonar:** "Start SonarQube" serves `localhost:9000` from your folder. The skill in an agent sandbox prints bloat, conventions and bugs, with no security findings.
- **API key:** `git grep ANTHROPIC_API_KEY` finds only the historical mention in `ClaudeRunResult.cs` and its tests.
