---
name: write-spec
description: Produce a requirements-and-design spec.md from an accepted intent.md, constrained by the organisation's policy skills, and flag concerns. Use when an intent.md has been merged or when asked to write or update a spec. Owner - product owner with tech lead. Source of truth - this file.
---

# write-spec (Stage 2 — Design)

Input: a merged `work/<id>-<slug>/intent.md`. Output: `spec.md` in the same folder, opened as a PR.

## Steps
1. Read `intent.md`, `CLAUDE.md`, and apply the `security-policy` skill as a hard constraint.
2. **Deterministic first:** call `match_template` for each capability the intent needs. For every crystallized match,
   the spec *references* the template (`templates/<id>`, version, parameters) instead of re-specifying it. Read the
   template's `aico://templates/<id>/context.md` resource to confirm it fits ("When NOT to use it").
3. Write `spec.md`:
   - **Requirements** — numbered, testable (Given/When/Then where useful).
   - **Design** — components, data, interfaces; only for what no template covers.
   - **Reused deterministic flows** — table: capability · template@version · parameters.
   - **Non-functional** — performance, security, privacy, observability.
   - **Test strategy** — unit, integration, evals touched.
   - **Concerns** — numbered, each with owner (product owner / policy owner / tech lead) and a proposed resolution.
4. Open a PR `spec: <slug>` labelled `stage:spec`. Request review from the product owner and, for concerns, the policy owner.

## Rules
- Flag, don't decide: concerns go to their owners; never silently resolve a compliance question.
- Prefer extending an existing template (propose a new version) over specifying a near-duplicate.
