# Runbook: roll back a release (pre-approved)

**When:** a 3σ anomaly on a post-deploy metric correlates with the latest release, or on-call decides to roll back.
**Who may run it:** on-call engineer. Agents may *propose* it in the anomaly issue; they never run it on `prod`.

## One line
```bash
gh workflow run deploy.yml -f environment=prod -f ref=<previous-tag>
```
The `prod` environment still requires reviewer approval in GitHub — approve it in the Actions run.

## After
1. Confirm the metric returns inside 1σ (next `maintain-detect` run, or run it with `gh workflow run maintain-detect.yml`).
2. Write the incident record into the anomaly issue; the fix follows the normal loop (intent → spec → plan → PR).
3. Add a regression eval for the cause (`evals/`), so it cannot ship again unnoticed.

Rehearse this monthly on `dev` (`-f environment=dev`).
