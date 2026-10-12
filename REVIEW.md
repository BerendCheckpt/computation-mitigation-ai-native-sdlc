# REVIEW.md — PR review policy

Applied to every PR by Claude Code Review (`.github/workflows/claude-review.yml`) and by human reviewers.
Derived from our QA requirements. Owner: tech lead (CODEOWNERS).

## When AI review runs
- **Skipped** when `template-verify` reports `template_only=true`: every changed file regenerates byte-identically from a
  crystallized, human-approved template. Deterministic verification replaces the model call.
- **Runs** on every other PR, but only on hand-written or non-verified lines. Verified generated files are listed as
  "verified, not reviewed".

## Severity ranking (report findings in this order)
1. **Blocker** — security (injection, authz bypass, secrets, unsafe deserialization), data loss, broken build/tests,
   a change that contradicts the approved `spec.md`/`plan.md`.
2. **Major** — correctness bugs, missing tests for changed behaviour, unhandled errors on external calls,
   breaking API changes without versioning.
3. **Minor** — readability, naming, duplication, missed reuse of an existing template or helper.
4. **Nit** — formatting only; never block on nits.

## Always check
- The diff matches the committed `plan.md` (files and order of work). Report out-of-plan files.
- New behaviour has a failing-first test (bug fixes) or unit tests (features).
- No edits to `aico:generated` files without removing the marker; if the marker was removed, ask why the template
  could not be used and whether the template should evolve.
- Repeated hand-written patterns → suggest crystallizing (`crystallize-template` skill).

## Human review is mandatory
- PRs labelled `critical` or `regulated`, or touching `policy.yaml`, `.claude/**`, `.github/workflows/**`,
  `templates/**`, authentication/authorization, payments, or personal data.
- AI findings are advice; the human reviewer approves. Use `@claude` in review comments to ask for fixes or explanations.

## Feedback loop
Findings that recur become a line in `CLAUDE.md` ("Known mistakes") or a hook — not a longer review prompt.
