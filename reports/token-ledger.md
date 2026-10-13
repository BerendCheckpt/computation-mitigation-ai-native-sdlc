# Token ledger

Generated from `ledger/token-usage.jsonl` by `dotnet run scripts/aico-ci.cs -- ledger report`. Do not edit by hand: the ledger is the source of truth and its git history is the audit trail.

Token counts come from each run's final `result` message (`modelUsage`, which includes subagents). `total_cost_usd` is a client-side estimate from the CLI's price table, not billing data; use the Claude Console or the Usage and Cost API for invoices.

## Summary

| | Value |
|---|---|
| Runs recorded | 0 (0 model path, 0 deterministic path) |
| Tokens spent on the model path | 0 (output 0) |
| Estimated cost of the model path | $0.00 |
| Comparisons | 0 (0 accepted) |
| Period | no runs yet |

## Current measured savings per template

Latest accepted comparison per template version. These figures feed the KPI report as "measured".

| Template | Output tokens: model → deterministic | Saved output | Saved total tokens | Saved cost (est.) | Correspondence | Measured |
|---|---|---|---|---|---|---|
| _none yet_ | | | | | | |

Crystallized but not measured: minimal-api-endpoint@1.0.0 (no recorded model run to compare against yet). KPIs use the template's `estimatedOutputTokens` for these, labelled "estimated".

## Comparison history

| Date | Template | Baseline run | Deterministic run | Saved output | Saved total | Accepted | Notes |
|---|---|---|---|---|---|---|---|
| _none yet_ | | | | | | | |

## Runs by source

| Source | Path | Runs | Output | Input | Cache read | Cache write | Cost (est.) |
|---|---|---|---|---|---|---|---|
| _none yet_ | | | | | | | |

## Recent runs

| Date | Run | Source | Path | Template | Model | Output | Total tokens | Cost (est.) | Result |
|---|---|---|---|---|---|---|---|---|---|
| _none yet_ | | | | | | | | | |
