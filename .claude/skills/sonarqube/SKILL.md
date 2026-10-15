---
name: sonarqube
description: Scan the whole project with the local SonarQube server for bloated code, convention violations and bugs (no security rules), after code has been written and before a PR is opened. Use when an implementation step is finished, when asked to "run sonar" or "check code quality", or as the last step of the Definition of done. Never part of a GitHub workflow. Owner - tech lead.
allowed-tools: Bash(dotnet sonarscanner:*), Bash(dotnet build:*), Bash(dotnet run scripts/aico-ci.cs:*)
---

# sonarqube

Local only. The scan runs inside the agent's sandbox. The server runs in the `aico-sonarqube` sandbox, which the
engineer starts from the hub ("Start SonarQube"). That server runs the distribution installed at
`~/Documents/Tools/Sonarqube` (`%USERPROFILE%\Documents\Tools\Sonarqube` on Windows), or at `SONARQUBE_HOME`
if that is set. Any machine with SonarQube at that location works.

## When
- Only **after** code is written: never before or during an implementation step.
- Before a PR is opened, once per change.
- Never in `.github/workflows/`.

## Steps
1. Check that `SONAR_HOST_URL` is set; the sandbox sets it to `http://host.docker.internal:9000`. If the server
   does not answer, stop and ask the engineer to press "Start SonarQube" in the hub. Do not try to start it yourself.
2. Analyse the whole project:
   ```bash
   dotnet sonarscanner begin /k:cnu-aico /d:sonar.host.url="$SONAR_HOST_URL"
   dotnet build Cnu.AICO.slnx --no-incremental
   dotnet sonarscanner end
   ```
   The token comes from `SONAR_TOKEN`, which the sandbox proxy injects from the engineer's `sbx secret set sonar`.
   Never ask for the token, never type it, and never print it.
3. Report the findings:
   ```bash
   dotnet run scripts/aico-ci.cs -- sonar
   ```
   It lists Maintainability and Reliability issues for the whole project, grouped as **Bloat** (size, complexity,
   duplication, dead code), **Conventions** and **Bugs**. Security issues and hotspots are out of scope and never
   requested.

## Afterwards
- Show the grouped report. **Fix nothing unless the engineer asks**, and then only the findings they name.
- Exit code **2**: there is a Blocker/High (legacy Blocker/Critical) finding, which counts as **critical** (see
  `CLAUDE.md` Terminology). Stop. Make no autonomous fix. Tell the engineer, and give the PR the `critical` label so
  a human reviews it (`policy.yaml` → human).
- Exit code **1**: the server could not be reached or the token was rejected. Report it and stop.
