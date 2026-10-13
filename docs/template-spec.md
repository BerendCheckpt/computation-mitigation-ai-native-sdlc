# Template spec reference and authoring guide

A template turns a proven solution into something a generator can produce **without a model**. It is a folder under
`templates/<id>/`. The worked example is [`templates/minimal-api-endpoint`](../templates/minimal-api-endpoint).

```
templates/<id>/
  spec.yaml                      # the contract (this document)
  context.md                     # why, design decisions, constraints, WHEN NOT TO USE IT
  content/*.scriban              # the code
  tests/unit/*.scriban           # unit tests shipped with the code (required)
  tests/integration.suggested/*  # integration tests suggested wherever applied (required)
  provenance.json                # source PRs, runs + gate results, approvals, successful prompts
```

## `spec.yaml` fields
| Field | Required | Meaning |
|---|---|---|
| `id` | ✓ | kebab-case, equals the folder name; the MCP tool is `generate_<id with _>` |
| `version` | ✓ | semver. **Bump it for any content change**, and regenerate golden files in the same PR |
| `state` | ✓ | `exploratory · candidate · approved · crystallized · deprecated`. Only humans set `approved`/`crystallized` |
| `title`, `description` | ✓ / – | `title` is shown in the MCP tool; keep it short (tokens) |
| `owners` | ✓ | Template owners (also in CODEOWNERS) |
| `estimatedOutputTokens` | – | Judgement-based guess of what a model would spend writing this by hand. Fallback only; KPIs label it "estimated" |
| `measuredOutputTokens` | – | `{ baseline, deterministic, n, model, date, evidence }` from the measure-baseline workflow. Replaces the estimate; KPIs label it "measured". The validator requires n ≥ 3 and a committed evidence file |
| `matchThreshold` | – | Overrides the global minimum confidence (0–1) |
| `intent.keywords` | ✓ | Phrases describing the intent; normalized into the BM25 document (incl. bigrams) |
| `intent.synonyms` | – | `canonical: [alternatives]`. Alternatives are rewritten before scoring |
| `intent.negatives` | – | Words that veto a match (`design`, `why`, `refactor` …): ideation, not generation |
| `parameters[]` | ✓ | The contract; becomes the MCP `inputSchema` |
| `requirements`, `preconditions` | – | Human/agent-readable; preconditions are returned by `generate_*` as next steps |
| `outputs[]`, `tests.unit[]`, `tests.integrationSuggested[]` | ✓ | `{ template, path }`; `path` is a Scriban expression relative to the repo root |
| `postActions` | – | Commands to run after generation (rendered with the parameters) |

### Parameters
```yaml
- name: entity            # camelCase
  type: string
  required: true
  description: Singular entity name in PascalCase, e.g. Order.   # shown to the model in the tool schema
  pattern: "^[A-Z][A-Za-z0-9]*$"                                  # validation; also blocks injection into paths/code
  extract: "\\b(?:entity|resource|model)\\s+([A-Za-z][A-Za-z0-9]*)" # one capture group; prompt → value, no model
  transform: pascal       # pascal | camel | lower | kebab
- name: route
  default: "/{{ entity | string.downcase | plural }}"             # derived default (Scriban)
```
Design rules: few parameters, all validated, derived defaults where possible. If a value cannot be validated with a
regex, the template is probably too broad.

## Content (Scriban)
- `StrictVariables` is on: an unknown variable is an error, never an empty string.
- Helpers: all Scriban built-ins (`string.downcase`, …) plus `plural` and `camel`.
- Output is normalised to LF. Each code/unit-test file gets a first-line `aico:generated` marker (template, version,
  source, base64 parameters); suggested integration tests get `aico:suggested`.
- C# braces are fine (`{ }`, `$"{x}"`); only `{{ … }}` is template syntax.

## Tests that ship with a template
- **Unit tests** (`tests.unit`) are generated next to the code and verified like code. They must pass in the template
  self-test (`scripts/template-selftest.sh`) for every version.
- **Suggested integration tests** (`tests.integrationSuggested`) are emitted wherever the template is applied. Engineers
  adapt them (auth, fixtures, data) and delete the `aico:suggested` line. They are reviewed as hand-written code.

## `provenance.json`
Evidence for promotion and the input for model-free learning:
`sourcePullRequests`, `approvals[] {by, role: template-owner|tech-lead, at, pullRequest}`,
`runs[] {runId, at, pullRequest, gates{…}}`, `successfulPrompts[] {prompt, parameters}`, `demoted`, `demotionReason`.
Written by tooling (`record_outcome`). Agents may not edit it by hand (protect-paths hook).

## Validation
`dotnet run scripts/aico-ci.cs -- validate` (CI) checks the schema-level rules plus:
- referenced files exist and regexes compile;
- the template renders with sample parameters;
- unit and suggested integration tests are present;
- `approved`/`crystallized` templates have ≥ 3 passing runs and both approvals recorded.

Editors: add `# yaml-language-server: $schema=../../schemas/template-spec.schema.json` at the top of `spec.yaml`.

## Authoring checklist
1. Evidence: ≥ 3 merged PRs with passing gates (the `crystallize-template` skill does the diffing).
2. Write the spec, content, tests and context; `state: candidate`, `version: 0.1.0`.
3. `validate`, then `template-selftest`, then add routing cases to `evals/routing.yaml` (prompts that must and must not match).
4. PR → template owner and tech lead approve → later PR sets `state: crystallized`.
5. Change = version bump + `AICO_UPDATE_GOLDEN=1 dotnet test tests/Cnu.AICO.Core.Tests` + reviewed golden diff.
