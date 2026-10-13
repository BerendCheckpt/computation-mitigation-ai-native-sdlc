# AI-native SDLC on GitHub with Claude

How CheckNet United runs the AI-native SDLC on GitHub with Claude, with computation mitigation built in.
The deterministic layer is described in [computation-mitigation.md](computation-mitigation.md).

**Principle:** agents do the work and humans steer. Every stage ends in a committed artifact, and that artifact
triggers the next stage. A model is used only where reasoning adds value. Everything that has been proven and
approved runs deterministically.

```
Human sets intent ─▶ Agent drafts artifact ─▶ Human approves at gate
       ▲                                              │
Human triages ◀─ Agent escalates ◀─ Agent executes within hooks & verifies its own work
```

Legend: ◆ = deterministic (no model) · ● = model

---

## Part A — GitHub platform setup (one-time, per org/repo)

1. **Claude GitHub App and `anthropics/claude-code-action@v1`.** Run `/install-github-app` from Claude Code, or install the app from GitHub. Store `ANTHROPIC_API_KEY` as an organisation secret and scope it to the repos that use it. Workload identity federation (OIDC) is the preferred alternative to a static key.
2. **Ruleset on `main`:**
   - PR required with 1 approval and CODEOWNERS review required.
   - Required status checks: `ci / build-test`, `template-verify / verify`.
   - Linear history; force-push and deletion blocked.
3. **CODEOWNERS** ([.github/CODEOWNERS](../.github/CODEOWNERS)) maps the playbook roles onto GitHub:
   - product owner → `intent.md`/`spec.md`
   - tech lead → `plan.md`, `.claude/**`, `REVIEW.md`, `CLAUDE.md`
   - security → `policy.yaml`, `security-policy` skill
   - platform team (template owners) → `templates/**`
4. **Labels.** These are the shared vocabulary of the agents and the deterministic layer: `stage:intent`, `stage:spec`, `stage:plan`, `stage:build`, `stage:maintain`, `critical`, `regulated`, `crystallized`, `agent-fix-allowed`, `escalated`, `dismissed`, `kpi`.
   ```bash
   for l in stage:intent stage:spec stage:plan stage:build stage:maintain critical regulated crystallized agent-fix-allowed escalated dismissed kpi; do gh label create "$l" --force; done
   ```
5. **Environments.** There are two, `dev` and `prod`:
   - `dev` deploys automatically after CI passes on `main`. Claude can cause a dev deploy only through a merged PR.
   - `prod` requires reviewers. A human approves every release, and Claude is never autonomous on prod.
   - Rollback is one line (see [ops/runbooks/rollback.md](../ops/runbooks/rollback.md)).
6. **Managed settings** (deployed by admins, outside the repo). These hold the hooks users must not override: `no-secrets`, `protect-paths`, and the prod-deploy gate. The repo's `.claude/settings.json` contains the team-shared versions.
7. **Issue forms.**
   - "New intent", filed by **requirements analysts**: problem, affected users, what better means, out of scope, constraints, criticality.
   - "Anomaly", filed by the detection workflow.
8. **Secrets and permissions per workflow.** Every workflow declares least-privilege `permissions:`. Read-only judgement steps come first, and write steps exist only behind existing gates (PR + CODEOWNERS + required checks).

## Part B — Stage by stage

| Stage | Trigger | What runs | Artifact / gate | Mitigation |
|---|---|---|---|---|
| **1 Plan** | A requirements analyst opens a "New intent" issue (label `stage:intent`), which starts `stage-1-intent.yml`. A chat session also works. | ● Claude picks up the issue with the `write-intent` skill. It asks at most 5 clarifying questions in one comment; the analyst answers there and mentions `@claude`. Claude then opens an `intent.md` PR linked to the issue. | PR; the product owner's merge is the acceptance | Sonnet tier; ◆ `match_template` notes existing deterministic flows |
| **2 Design** | Merge touching `work/**/intent.md` | ● `stage-2-spec.yml` runs Claude with `write-spec` and the `security-policy` skill, and flags concerns | `spec.md` PR; product owner and policy owner approve | ◆ `match_template` runs first; a crystallized template is *referenced*, not re-specified |
| **3 Build** | Merge of `spec.md` | ◆ `stage-3-plan-notify.yml` opens a "plan" issue (no model). An engineer runs a **plan-mode** session, commits `plan.md` (tech lead approves), then implements. | `plan.md` PR; ◆ `plan-scope` hook | ◆ crystallized parts come from `generate_*` (~50 tokens vs thousands); ● only the novel parts |
| **4 Test** | Every push | ◆ build, tests, golden files, routing evals, template validation, template self-test (`ci.yml`) | Required checks | ◆ template output verified by **regenerate + diff** |
| **5 Deploy / Review** | PR opened or updated | ◆ `template-verify.yml`, then ● `claude-review.yml` following `REVIEW.md` (findings ranked by severity). Humans review `critical`/`regulated`. `@claude` in PR comments for fixes. | Ruleset approvals; prod environment gate; one-line rollback | ◆ **AI review skipped for template-only PRs**; ● review only hand-written lines |
| **6 Maintain** | Hourly `maintain-detect.yml` | ◆ detection script (mean/σ). `ops/bands.md` bands: 1σ log · 2σ ● read-only diagnosis → anomaly issue · 3σ ● PR through review, or a pre-approved runbook | Diagnosis is written as a new intent; humans triage | ◆ runbooks are scripts; ● fixes only when not `critical` (otherwise human) |
| **6 Maintain (failed CI run)** | A `ci` or `template-verify` run fails (`workflow_run` in `maintain-detect.yml`, same-repo runs only) | ● headless Claude reads `gh run view --log-failed` read-only and comments the root cause and a minimal fix on the PR | Engineer applies the fix through the PR | ● Sonnet, max 10 turns, no edit tools; one run per commit |
| **6 Maintain (filed defects)** | Someone labels an issue `agent-fix-allowed` (`stage-6-agent-fix.yml`); later `@claude` comments continue it | ● Claude finds the root cause, writes a failing test first, fixes it and opens a `Fixes #N` PR. `critical` → ◆ policy comment, no model | Fix PR through template-verify, review and CODEOWNERS | ● model only for non-critical fixes; ◆ `match_template` first |

### Artifact chain (what triggers what)
- Issue `stage:intent` → `intent.md` PR → merge → **spec workflow** → `spec.md` PR → merge → **plan issue** → `plan.md` PR → merge → implementation PR → `template-verify` (+ AI review if needed) → merge → `ci` → `deploy` (dev auto, prod gated).
- Anomaly ≥ 2σ → anomaly issue (intent draft) → triage → back to Stage 1.
- Defect issue (bug with a stack trace or clear reproduction) → label `stage:maintain` + `agent-fix-allowed` → fix PR. No intent or spec: a bug fix restores intended behaviour, so it needs no new design. Use the intent path (Stage 1) only when the "bug" is really a missing or changed requirement.

### Which stage fits an issue?
| The issue is… | Labels | Path |
|---|---|---|
| A new capability, or a change to intended behaviour | `stage:intent` | Stage 1 → 2 → 3 |
| A defect in existing behaviour, non-critical, with enough to reproduce | `stage:maintain`, `agent-fix-allowed` | Stage 6 agent fix → PR |
| A defect that is customer-facing, data-affecting or a security issue | `stage:maintain`, `critical` | Human fix → human-reviewed PR |
| Found by a metric breach | (filed by `maintain-detect`) | Triage, then one of the rows above |

### Stage 1 in detail (requirements analysts)
1. The analyst opens **New intent** and fills in the problem, not the solution.
2. Within a minute Claude either asks its questions in one comment, or opens the intent PR straight away.
3. The analyst corrects misunderstandings in the PR (comment `@claude …` or edit directly).
4. The product owner merges, and the merge records author and timestamp. Design starts automatically.

### Stage 3 in detail (engineers)
1. `claude --permission-mode plan`. Give Claude `work/<id>/intent.md` and `spec.md`, and ask for the files to change, the order of work, and the tests.
2. Question the plan: look for breaking changes, risky steps, and options left out. Iterate until another engineer could follow it.
3. Commit `plan.md` (tech lead approves). Optionally run `echo work/<id>/plan.md > .aico/active-plan` to turn on the plan-scope hook.
4. Implement. The `UserPromptSubmit` hook and the MCP server's instructions push crystallized parts to `generate_*`.
5. Split independent plan steps into parallel sessions, each in its own worktree. Recurring jobs go to subagents (`test-writer`, `incident-diagnoser`, `template-curator`).
6. **Auto mode** becomes viable once `CLAUDE.md`, the hooks and the test suite are mature: Claude implements right after plan approval.

### Guardrails summary
| Layer | Advisory | Deterministic |
|---|---|---|
| Context | `CLAUDE.md`, skills | — |
| Edits | — | hooks: `protect-paths` (incl. generated files), `no-secrets`, `plan-scope`, `format` |
| Routing | MCP server instructions | `match_template`, `policy.yaml` (critical → human) |
| Merge | AI review findings | CODEOWNERS, required checks, `template-verify` |
| Deploy | — | `prod` environment reviewers, one-line rollback |

## Part D — Measurement

**Playbook indicators** (from git and GitHub timestamps):
| Stage | Leading | Lagging |
|---|---|---|
| Plan | Time from intent capture to commit | Intent acceptance rate; changes after the first `spec.md` commit |
| Design | Intent→spec time vs the old requirements cycle | `spec.md` commits after the first plan commit |
| Build | First-pass merge rate; approval → merged PR time | Rework cycles; diff-matches-`plan.md` rate |
| CLAUDE.md | Mistakes caught | Caught mistakes that are not repeated |

**Mitigation KPIs** (`kpi-report.yml`, weekly PR with `reports/kpi-<week>.md`):
- share of requests served deterministically (`generate_*` + skipped reviews ÷ all outcomes)
- model escalations, and policy routes to humans
- AI reviews skipped by regen-verify
- output tokens avoided, split into **measured** (from token-ledger comparisons of real runs: deterministic run vs the earlier model-path run) and **estimated** (templates without such a comparison yet). The audit log and overview are `ledger/token-usage.jsonl` and `reports/token-ledger.md`. See [computation-mitigation.md §9](computation-mitigation.md#9-measuring-avoided-tokens).
- kWh / CO₂e: always an estimate, using a configurable factor published with the report.
- templates per lifecycle state, and demotions

## Part E — Rollout phases

1. **Foundation:** rulesets, CODEOWNERS, labels, `CLAUDE.md`, `REVIEW.md`, managed hooks, and AI review on PRs (read-only).
2. **Artifact chain:** `write-intent` and `write-spec` skills, the Stage 1–3 workflows, and the evals suite in CI.
3. **Deterministic layer:** MCP server, the first 2–3 templates crystallized from real proven PRs, `template-verify`, and AI-review skipping.
4. **Maintain loop:** detection script, `bands.md`, 2σ read-only diagnosis, 3σ PR/runbook, critical → human.
5. **Optimise:** tune thresholds from the escalated/dismissed data, apply model tiering (ADR 0004), and publish KPIs. Re-evaluate the optional router (ADR 0003) once automated volume justifies it.
