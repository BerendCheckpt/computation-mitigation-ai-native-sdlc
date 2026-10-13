---
name: create-pr
description: Create a branch, commit, verify CI-sensitive files, and open a PR
---
1. Create a branch named `<type>/<short-desc>` from main.
2. Stage only the files related to the change. Run `git status --ignored` to confirm no needed file is ignored.
3. If .github/ changed, run `actionlint`.
4. Commit with a conventional commit message.
5. Push and run `gh pr create --fill`, then `gh pr checks --watch`.
6. If checks fail, show the failing log with `gh run view --log-failed` and propose a fix. Do not merge.