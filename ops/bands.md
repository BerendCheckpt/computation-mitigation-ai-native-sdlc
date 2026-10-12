# Response bands (Stage 6 — Maintain)

Deterministic detection (`scripts/aico-ci.cs detect`, mean and sample standard deviation over a rolling baseline of
≥ 7 points) decides **whether** something is wrong. A model is involved only at higher bands, and only through gated
routes. Owner: SRE + tech lead (CODEOWNERS). Changes to this file change agent autonomy, so review them as such.

| Band | Condition | Action | Model? | Route |
|---|---|---|---|---|
| Normal | < 1σ | nothing | no | — |
| **Log** | ≥ 1σ | record in the workflow summary | no | — |
| **Diagnose** | ≥ 2σ | `incident-diagnoser` (read-only) writes an anomaly issue with an intent draft | yes (Sonnet) | issue → human triage |
| **Act** | ≥ 3σ | Claude opens a fix PR through the review gate, or names a pre-approved runbook | yes (Sonnet) | PR → template-verify → review → CODEOWNERS |

## Metrics
| Metric | Direction | Baseline | Critical? |
|---|---|---|---|
| `ci_failure_rate` (main, per day) | higher is worse | last 14 days | no |
| `post_deploy_5xx_rate` (prod) | higher is worse | last 14 deploys | **yes**: at Act, the policy routes to a human, with no autonomous fix |

## Rules
- `critical` metrics or issues: no autonomous model or tool action (`policy.yaml`, rule `critical-needs-human`).
- Agents never deploy and never run runbooks on `prod`; they name the runbook, and on-call runs it.
- Triage outcomes (fix now / schedule / dismiss) are recorded on the issue. Repeated dismissals of a band mean the
  band is too sensitive: tune the baseline window or the metric, never silence it.
- Every shipped fix gets a regression eval (`evals/`).
