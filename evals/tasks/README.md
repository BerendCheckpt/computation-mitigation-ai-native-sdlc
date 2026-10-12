# Model-agnostic task evals

The playbook's continuous evals: 20–50 **real, recent** tasks with their acceptable outcomes, run on a schedule and
whenever `CLAUDE.md`, a skill or a template changes. Results gate those changes.

Two suites, cheapest first:

| Suite | File | Model? | Runs |
|---|---|---|---|
| Routing | `evals/routing.yaml` | **No** — asserts which route (deterministic / model) and parameters a prompt gets | every push (`ci.yml`, via `RoutingEvalTests`) |
| Tasks | `evals/tasks/*.yaml` | Yes — `claude -p` performs the task in a sandbox, then deterministic checks judge it | nightly / on `CLAUDE.md` or skill change |

## Task format
```yaml
id: add-retailer-endpoint
source: org/repo#123            # the real task this came from
prompt: "Add a minimal API endpoint for entity Retailer in project Cnu.Backoffice.Api"
acceptable:                      # all must hold; each is a deterministic check
  - command: dotnet build
  - command: dotnet test
  - files_exist: [src/Cnu.Backoffice.Api/Features/Retailers/RetailerEndpoints.cs]
  - decision_log: { tool: generate_minimal_api_endpoint }   # proves the cheap path was taken
```
For a task a crystallized template covers, the most important check is the last one: the session must have used the
template tool instead of writing the code. A regression there means wasted computation even if the code is correct.

When a production incident happens, the responsible team adds a task here; it becomes a permanent regression test.
