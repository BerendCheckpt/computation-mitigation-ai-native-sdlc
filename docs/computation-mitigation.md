# Computation mitigation: the deterministic layer

**Problem.** Caching model *results* still sends every prompt to a model. Once a workflow has crystallized, meaning it
was approved, refined and proven repeatedly, invoking a model for the same kind of prompt wastes compute, money and
energy. It also adds non-determinism to work that should be reproducible.

**Rule.** Models are used for ideation, intent/spec drafting, requested feedback, read-only diagnosis, and
**non-critical** agent fixes (`policy.yaml`). Everything crystallized runs deterministically.

## 1. Crystallization lifecycle
```
Exploratory ──▶ Candidate ──▶ Approved ──▶ Crystallized ──▶ Deprecated
                    ▲                            │
                    └──────── demotion ──────────┘  (repeated gate failures)
```
| State | Entry criterion | Who | Effect |
|---|---|---|---|
| Candidate | Pattern passed the Stage-4 gates on **≥ 3** independent PRs (`provenance.json`) | `crystallize-template` skill drafts it | Visible in match results, never served |
| Approved | Template PR approved by **template owner and tech lead** (CODEOWNERS) | Humans; cannot be skipped | Validator checks the approvals are recorded |
| Crystallized | PR setting `state: crystallized` merged | Humans | Advertised in `ListTools`; served deterministically; AI review skipped for regen-verified output |
| Demoted | ≥ 2 consecutive gate failures recorded through `record_outcome` | Automatic (the safe direction) | Effective state becomes Candidate until a reviewed PR fixes the template |

## 2. A template is an executable spec
See [template-spec.md](template-spec.md). In short: `spec.yaml` (contract: parameters, validation, extraction, intent
keywords, outputs, preconditions), `context.md` (why, and when NOT to use it), Scriban content, **shipped unit tests**,
**suggested integration tests**, and `provenance.json` (evidence). The parameters section is projected directly into
the MCP tool's JSON `inputSchema`, so a model, a pipeline and a script all fill in the same contract.

## 3. MCP server `Cnu.AICO.McpServer`
.NET 10 with the official `ModelContextProtocol` C# SDK. Stdio is the default (Claude Code, through `.mcp.json`);
`--http <url>` is for shared/CI use.

| Tool | Deterministic | Purpose |
|---|---|---|
| `match_template(prompt, context?, labels?)` | ◆ | Routing decision: `deterministic` (tool + parameters), `model` (with best candidate's context resource), or `human` (policy) |
| `generate_<id>(params…)` | ◆ | One per **crystallized** template. Writes code + unit tests + suggested integration tests; `dryRun` returns contents |
| `export_script(templateId, parameters, shell)` | ◆ | Stand-alone bash/PowerShell that reproduces the flow with no MCP and no model |
| `record_outcome(templateId, runId, gates, …)` | ◆ | Provenance, promotion eligibility, demotion, keyword learning |

Resources: `aico://templates/<id>/context.md` and `…/spec.yaml`. Curated context for when a model *does* reason.

Token hygiene: `ListTools` shows only crystallized templates and keeps descriptions short. Tool definitions are sent
with **every** model request, so each word there costs on every call.

Policy: the server enforces `policy.yaml`. A `critical` label in `agent-fix`/`diagnose`/`ci` context refuses both
model reasoning and tool generation, and routes the work to a human PR. The `ci` context is deterministic-only.

Telemetry: every decision goes to JSONL (`CNU_AICO_LOG`) and feeds the weekly KPI report. Each deterministic
generation records the tokens it avoided and whether that figure is **measured** or **estimated** (section 9).

## 4. Model-free keyword matching (`Cnu.AICO.Core.Matching`)
1. **Normalize:** lowercase, tokenize, drop stop words, light suffix stemming, rewrite synonyms to canonical terms.
2. **Exact cache:** SHA-256 of the normalized prompt against previously approved prompts.
3. **Negatives veto:** words like *design, why, explain, refactor, investigate* mark ideation work.
4. **Slot extraction:** each parameter's `extract` regex; slot values are removed from the query before scoring.
5. **Rank with BM25** (appendix A1) over each template's keyword document: spec keywords plus bigrams, plus the
   **TF-IDF** top terms mined from that template's successful prompts (appendix A2).
6. **Decide:** deterministic only if the template is crystallized, coverage ≥ threshold (default 0.6), it leads the
   crystallized runner-up by ≥ 20 %, no candidate clearly outscores it, and all required parameters resolve and validate.
   Anything else escalates to a model, together with the best candidate's context.
7. **Learning stays model-free:** `record_outcome` adds prompts of passing runs to `successfulPrompts`, which updates the
   TF-IDF terms and the exact cache.

Routing quality is guarded by [`evals/routing.yaml`](../evals/routing.yaml), which runs on every push.

## 5. Why the MCP server alone already saves compute
- **Output tokens are the expensive part.** A 300-line endpoint with tests is thousands of output tokens. A
  `generate_*` call is about 50 tokens of arguments. The model stops *writing* and starts *selecting and parameterising*.
- **Fewer turns.** One tool call replaces the read → write → build → fix loop, and with it the repeated re-sending of
  input context.
- **Determinism.** Same parameters, same bytes. Gates pass the first time; reviewers diff against a known template;
  template-only PRs skip AI review entirely (regenerate + diff).
- **Cheaper escalations.** When a model is needed, it gets the curated `context.md` instead of exploring the repo.

## 6. Claude-side wiring
- `UserPromptSubmit` hook (`.claude/hooks/aico-hook.cs match`): on a confident match, it injects *"call
  `generate_<id>` with {params}"* so the session takes the cheap path.
- `PreToolUse` hooks: `protect-paths` (also blocks hand edits of `aico:generated` files), `no-secrets`, `plan-scope`.
  `PostToolUse`: `format`.
- Skills: `write-intent`, `write-spec`, `security-policy`, `crystallize-template`. Subagents: `test-writer`,
  `incident-diagnoser` (read-only), `template-curator` (Haiku).
- Model tiering: [ADR 0004](adr/0004-model-tiering.md).

## 7. Commands and scripts for deterministic flows
Every crystallized flow can run with **no model**:
- `dotnet run scripts/aico-ci.cs -- generate <id> --out <dir> key=value …` (pipelines, engineers)
- `export_script` → a self-contained bash/PowerShell file that embeds the rendered files
- `dotnet run scripts/aico-ci.cs -- verify --base origin/main` (the regen-verify gate)

## 8. Optional: deterministic front-door router (not built)
See [ADR 0003](adr/0003-optional-mcp-client-router.md) and `samples/RouterClient`. It is an MCP client that sends
`CallToolRequest` directly on a confident match, so zero model tokens are spent, including tool-list tokens. It is
recommended for high-volume automated paths, and not planned for now.

## 9. Measuring avoided tokens
Savings are measured as a side effect of real work. There is no separate measurement job and no extra Claude call.

**The token ledger.** No workflow runs a model. Every agent session runs locally in its own sandbox. When a
session ends, the `SessionEnd` hook (`aico-hook.cs track`) records its usage as `ledger/fragments/<session>.jsonl`,
and the fragment is committed with the PR. The usage is read from the session transcript plus its sub-agents'
transcripts: assistant `message.usage`, counted once per message id. A headless `claude -p --output-format json`
result is also accepted, following the
[Agent SDK cost-tracking guide](https://code.claude.com/docs/en/agent-sdk/cost-tracking):
- tokens from `modelUsage`, which includes subagents (`usage` covers only the top-level loop);
- output tokens only from the result message, because per-step `output_tokens` are placeholders;
- `total_cost_usd` stored as a **client-side estimate**, never as billing data;
- a run counts as unreliable **only when a model was invoked** and its totals are zero, or it crashed with zeroed
  totals. Unreliable runs are never used as a baseline. A run that invoked no model validly records 0 tokens.

The weekly `kpi-report` merges the fragments into
`ledger/token-usage.jsonl` (append-only, committed) and regenerates `reports/token-ledger.md`. CI fails if the overview
is stale (`ledger check`). The ledger's git history is the audit trail: every change arrives through a reviewed PR,
and every entry carries its commit SHA and source (`local-session` for agent sandboxes).

**Baseline and comparison:**
1. **Baseline.** A run in which the model did the work (no template tool called) is recorded on the *model path*. It
   is attributed to the matcher's best candidate template for the request, even if that template is not crystallized
   yet.
2. **Deterministic run.** After the template is approved and crystallized, a later request routes to it, and the run
   calls `generate_<id>` (seen in the cnu-aico server's decision log of that job). It is recorded on the *deterministic
   path*.
3. **Comparison.** When the deterministic run enters the ledger, it is compared with the run it corresponds to: the
   latest successful model-path run with the **same normalized prompt** (`exact-prompt`), otherwise the latest one for
   the **same template** (`same-template`). The ledger stores the saved input, output and cache tokens and the saved
   cost estimate. Negative values are kept: they mean the template path cost more.
4. A comparison is **accepted** only if the deterministic run succeeded, actually called the template tool, and has
   reliable totals. Rejected comparisons stay in the ledger with the reason.

**What changes in reporting:** each generation logs `tokensAvoided` with `baselineSource: measured` (the latest accepted
comparison for that template **version**) or `estimated` (`estimatedOutputTokens`). The KPI report shows both totals
on separate rows. A new template version starts as "estimated" again until its first accepted comparison.

**What stays an estimate:**
- Templates without a measurement fall back to `estimatedOutputTokens`, labelled "estimated".
- Energy and CO₂e: providers do not publish per-request energy, so these use a configured factor and are always labelled
  estimates.
- Review tokens saved by skipping AI review on template-only PRs are counted (number of skips), not converted to tokens.

---

## Appendix A1 — BM25 (Best Matching 25)
BM25 is the ranking formula that search engines like Lucene and Elasticsearch use by default. `match_template` uses it
to score how well a prompt matches each template's keyword document, with plain arithmetic and no model:
- **Rare words count more.** "endpoint" says more about a prompt than "add" does (inverse document frequency).
- **Repeats help, but less each time.** The 5th occurrence adds much less than the 1st (saturation, `k1 = 1.2`).
- **Long keyword lists don't win by length alone** (length normalisation, `b = 0.75`).

Every template gets a score and the highest wins. That takes microseconds and about 60 lines of C#
([`Bm25Index.cs`](../src/Cnu.AICO.Core/Matching/Bm25Index.cs)). The result is used only above the confidence threshold,
and only when it clearly beats the runner-up. Otherwise the prompt goes to a model.

## Appendix A2 — TF-IDF (term frequency × inverse document frequency)
TF-IDF builds each template's keyword list from prompts that worked, without a model:
- **TF:** how often a word appears in this template's successful prompts.
- **IDF:** how rare the word is across all templates' prompts. A word every template has scores zero and is dropped.
- **TF × IDF:** words frequent for this template and rare elsewhere end up on top; the top 10 join the BM25 document.

Slot values (e.g. "Order", "Shop.Api") are removed first, so they never become keywords
([`TfIdfMiner.cs`](../src/Cnu.AICO.Core/Matching/TfIdfMiner.cs)).
