# minimal-api-endpoint — context

Curated context served as an MCP resource. A model that *does* need to reason about this area reads this
instead of re-exploring the codebase.

## Why it exists
CRUD endpoint groups were hand-written (or model-generated) over and over with small, review-costly differences:
inconsistent status codes, missing validation, handlers that could only be tested through a host. The pattern was
crystallized from PRs that passed every quality gate and was approved by the template owner and tech lead.

## Design decisions
- **Static handlers returning `TypedResults`** — unit-testable without `WebApplicationFactory`; the result type
  documents every status code in the signature (and in OpenAPI).
- **`I{Entity}Store` abstraction, in-memory default** — the endpoint layer never knows about persistence; swap the
  registration for an EF Core/Dapper store without touching endpoints or their tests.
- **Route group per entity** — one `Map{Entities}Endpoints()` call in `Program.cs`, tagged for OpenAPI.

## Constraints
- .NET 10, ASP.NET Core minimal APIs, nullable enabled.
- No business logic beyond "name is required" — anything richer is *not* this template.

## When NOT to use it
- Endpoints with domain rules, workflows, authorization policies per operation, or paging/filtering requirements.
- Anything that is not a single entity with an id and a name-like field.

In those cases the router escalates to a model, which receives this file as context and can still *start* from the
generated skeleton.

## Wiring after generation
```csharp
builder.Services.Add{Entity}Store();
app.Map{Entities}Endpoints();
```
