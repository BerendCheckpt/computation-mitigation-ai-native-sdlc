// aico:generated template=minimal-api-endpoint@1.0.0 source=content/Endpoints.cs.scriban params=eyJlbnRpdHkiOiJPcmRlciIsInByb2plY3QiOiJTaG9wLkFwaSIsInJvdXRlIjoiL29yZGVycyJ9
using Microsoft.AspNetCore.Http.HttpResults;

namespace Shop.Api.Features.Orders;

public static class OrderEndpoints
{
    public static IServiceCollection AddOrderStore(this IServiceCollection services) =>
        services.AddSingleton<IOrderStore, InMemoryOrderStore>();

    public static RouteGroupBuilder MapOrdersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/orders").WithTags("Orders");
        group.MapGet("/", List);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("/", Create);
        group.MapDelete("/{id:guid}", Delete);
        return group;
    }

    public static Ok<IReadOnlyCollection<Order>> List(IOrderStore store) => TypedResults.Ok(store.List());

    public static Results<Ok<Order>, NotFound> Get(Guid id, IOrderStore store) =>
        store.Find(id) is { } item ? TypedResults.Ok(item) : TypedResults.NotFound();

    public static Results<Created<Order>, ValidationProblem> Create(CreateOrderRequest request, IOrderStore store)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Name)] = ["Name is required."],
            });

        var created = store.Add(request.Name.Trim());
        return TypedResults.Created($"/orders/{created.Id}", created);
    }

    public static Results<NoContent, NotFound> Delete(Guid id, IOrderStore store) =>
        store.Remove(id) ? TypedResults.NoContent() : TypedResults.NotFound();
}
