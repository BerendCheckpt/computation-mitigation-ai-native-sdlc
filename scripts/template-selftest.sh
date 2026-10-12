#!/usr/bin/env bash
# Template self-test: generate each crystallized template into a scratch solution and run the tests it ships
# (and the integration tests it suggests). Proves the generated code compiles and passes — no model involved.
# Usage: bash scripts/template-selftest.sh [work-dir]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="${1:-$(mktemp -d)}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 CNU_AICO_LOG="$WORK/decisions.jsonl"

# --- minimal-api-endpoint -------------------------------------------------------------------------------
P="$WORK/minimal-api-endpoint"
mkdir -p "$P" && cd "$P"
dotnet new web   -n Shop.Api                  -o src/Shop.Api                  >/dev/null
dotnet new xunit -n Shop.Api.Tests            -o tests/Shop.Api.Tests            >/dev/null
dotnet new xunit -n Shop.Api.IntegrationTests -o tests/Shop.Api.IntegrationTests >/dev/null
rm -f tests/*/UnitTest1.cs
dotnet add tests/Shop.Api.Tests reference src/Shop.Api >/dev/null
dotnet add tests/Shop.Api.IntegrationTests reference src/Shop.Api >/dev/null
dotnet add tests/Shop.Api.IntegrationTests package Microsoft.AspNetCore.Mvc.Testing >/dev/null
# Unit tests use TypedResults types from the shared framework.
sed -i 's#<Project Sdk="Microsoft.NET.Sdk">#<Project Sdk="Microsoft.NET.Sdk">\n  <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>#' tests/Shop.Api.Tests/Shop.Api.Tests.csproj

(cd "$ROOT" && dotnet run scripts/aico-ci.cs -- generate minimal-api-endpoint --out "$P" --overwrite entity=Order project=Shop.Api)

# Wire-up per the template's preconditions.
cat > src/Shop.Api/Program.cs <<'EOF'
using Shop.Api.Features.Orders;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOrderStore();
var app = builder.Build();
app.MapOrdersEndpoints();
app.Run();
EOF

dotnet test tests/Shop.Api.Tests
dotnet test tests/Shop.Api.IntegrationTests
echo "template self-test passed: minimal-api-endpoint"
