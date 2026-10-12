---
name: crystallize-template
description: Turn a proven, repeatedly approved code pattern into a Candidate template (spec.yaml, context.md, Scriban content, shipped unit tests, suggested integration tests, provenance.json). Use when the same kind of change has been implemented and approved several times, when a reviewer suggests crystallizing, or when asked to create or evolve a template. Owner - platform team (template owners).
---

# crystallize-template

Goal: move a pattern from "model writes it every time" to "generator writes it, model only picks parameters".
You create a **Candidate**. Promotion to Approved/Crystallized is a human decision (template owner + tech lead).

## Steps
1. **Evidence**: list the merged PRs that implemented the pattern (≥ 3) and confirm their quality gates passed.
   Fewer than 3 → stop and say how many more are needed.
2. **Find the invariant**: diff the implementations. What varies becomes a parameter; everything else is template content.
   Keep parameters few, typed and validated (`pattern`). Prefer derived defaults (`default: "/{{ entity | string.downcase | plural }}"`).
3. **Create `templates/<id>/`** (kebab-case id, `version: 0.1.0`, `state: candidate`):
   - `spec.yaml` — follow `docs/template-spec.md`; add `intent.keywords`, `synonyms`, `negatives`, and an `extract`
     regex per parameter so prompts can be routed without a model.
   - `content/*.scriban` — the code, using only parameters (StrictVariables is on).
   - `tests/unit/*.scriban` — unit tests that ship with the code (required).
   - `tests/integration.suggested/*.scriban` — integration tests suggested wherever applied (required).
   - `context.md` — why, design decisions, constraints, **when NOT to use it**.
   - `provenance.json` — `sourcePullRequests`, past runs with gate results, and the prompts that led to them (`successfulPrompts`).
4. Run `dotnet run scripts/aico-ci.cs -- validate`, generate into a scratch project and run the shipped tests.
5. Open a PR `template: <id>` labelled `stage:build`; request the template owner and tech lead.

## Rules
- Never set `state: crystallized` yourself. Never copy secrets or retailer-specific data into a template.
- Changing an existing template = new `version`, regenerated golden files, and a note in `context.md`.
