#!/usr/bin/env bash
# Stand-in for the Claude Code CLI, for dry runs of scripts/measure-baseline.cs without spending tokens.
# Mimics `claude -p --output-format json`: the template arm (MCP config contains cnu-aico) "uses" the template and
# reports few output tokens; the control arm reports many.
#   CNU_AICO_CLAUDE=bash CNU_AICO_CLAUDE_PREFIX=tests/measure-baseline/fake-claude.sh \
#     dotnet run scripts/measure-baseline.cs -- --config tests/measure-baseline/smoke.yaml --out "$TMPDIR"
set -euo pipefail
mcp=""
while [ $# -gt 0 ]; do
  if [ "$1" = "--mcp-config" ]; then mcp="$2"; shift; fi
  shift
done
if grep -q cnu-aico "$mcp"; then
  mkdir -p .aico
  echo '{"tool":"generate_minimal_api_endpoint","route":"deterministic"}' >> .aico/decisions.jsonl
  out=$((250 + RANDOM % 100))
else
  out=$((3500 + RANDOM % 1500))
fi
printf '{"type":"result","subtype":"success","is_error":false,"duration_ms":1000,"num_turns":4,"result":"ok","total_cost_usd":0.01,"usage":{"input_tokens":100,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":%d}}\n' "$out"
