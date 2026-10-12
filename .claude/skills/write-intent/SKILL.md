---
name: write-intent
description: Turn a problem description or a "New intent" GitHub issue into a committed intent.md. Use when a requirements analyst or engineer describes a pain point, idea or request that is not yet an intent, or when an issue labelled stage:intent is picked up. Owner - product owner. Source of truth - this file.
---

# write-intent (Stage 1 — Plan)

You are acting as a requirements analyst's partner. Output: `work/<issue-number>-<slug>/intent.md`, opened as a PR
linked to the issue. Never start design or code in this skill.

## Steps
1. Read the issue (or chat) and restate the problem in one sentence.
2. Ask analyst-style questions **only for what is missing**, batched in one comment (max 5):
   scope, affected users, constraints (compliance, retailers, deadlines), definition of success, out of scope.
   In GitHub, post them as one issue comment and stop; resume when the analyst answers or mentions `@claude`.
3. Deterministic check: call `match_template` with the problem statement. If a crystallized template already covers
   it, say so in the intent under "Existing deterministic flows" — the design stage will reference it.
4. Write `intent.md` with exactly these sections:
   - **Problem** — what cannot be done today, for whom.
   - **Who is affected** — roles/retailers/systems.
   - **What better means** — measurable outcomes.
   - **Constraints** — technical, compliance, time.
   - **Out of scope**
   - **Existing deterministic flows** — templates found by `match_template` (or "none").
   - **Open questions**
   - **Source** — issue link, analyst, date.
5. Open a PR titled `intent: <slug>` with label `stage:intent`, body linking the issue. The product owner's merge is the acceptance.

## Rules
- Keep it under one page. No solutions, no technology choices.
- Correct, don't defend: if the originator says you misunderstood, rewrite the section.
