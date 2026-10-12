## What and why
<!-- Link the intent/spec/plan: work/<id>-<slug>/ -->

## Deterministic layer
- [ ] I checked `match_template` — this change is / is not served by a crystallized template.
- [ ] Generated files are untouched (or their `aico:generated` marker was removed deliberately — explain why).
- [ ] If this pattern was written by hand ≥ 3 times, a `crystallize-template` follow-up exists.

## Verification
- [ ] `dotnet build` and `dotnet test` pass locally
- [ ] Diff matches the committed `plan.md`
- [ ] Labels set: `critical` / `regulated` if applicable (forces human review)
