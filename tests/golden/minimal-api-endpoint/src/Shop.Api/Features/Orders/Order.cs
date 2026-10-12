// aico:generated template=minimal-api-endpoint@1.0.0 source=content/Entity.cs.scriban params=eyJlbnRpdHkiOiJPcmRlciIsInByb2plY3QiOiJTaG9wLkFwaSIsInJvdXRlIjoiL29yZGVycyJ9
namespace Shop.Api.Features.Orders;

public sealed record Order(Guid Id, string Name);

public sealed record CreateOrderRequest(string Name);
