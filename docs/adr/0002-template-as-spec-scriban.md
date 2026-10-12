# ADR 0002 — Templates are executable specs rendered with Scriban

- **Status:** Accepted
- **Date:** 2026-10-12

## Context
For a generator to be called *without* a model, everything a model would otherwise infer must be written down:
what the template is for, what varies, how to validate inputs, what it produces, how it is tested, and when not to
use it. That must be readable by humans (review), by machines (routing, MCP schema, validation) and by models (context
when they do reason).

## Decision
- One folder per template: `spec.yaml`, `context.md`, `content/*.scriban`, `tests/unit/*.scriban`,
  `tests/integration.suggested/*.scriban`, `provenance.json`. Format reference: `docs/template-spec.md`.
- `spec.yaml` is the **contract**: typed parameters with validation `pattern`, prompt `extract` regex, `transform`,
  derived `default`s; intent keywords/synonyms/negatives for routing; outputs; preconditions; post-actions.
- Rendering uses **Scriban** with `StrictVariables` (an undefined variable is an error, never an empty string) and LF
  line endings: same template version + same parameters ⇒ byte-identical output.
- Every generated code/test file carries a first-line `aico:generated` marker (template, version, source, parameters),
  which makes regenerate-and-diff verification possible. Suggested integration tests carry `aico:suggested` instead —
  engineers adapt them, so they are reviewed as hand-written.
- Templates must ship unit tests and suggest integration tests (`TemplateValidator` enforces it).

## Consequences
- **+** Specs double as documentation, MCP input schema and CI contract.
- **+** Hand edits to generated files are detectable (hook blocks them; CI fails on mismatch).
- **−** Scriban is a dependency of Core (small, MIT, widely used). It is not used by the router path at runtime.
- **−** Template changes require a version bump and regenerated golden files.
