// aico:generated template=minimal-api-endpoint@1.0.0 source=tests/unit/EndpointsTests.cs.scriban params=eyJlbnRpdHkiOiJPcmRlciIsInByb2plY3QiOiJTaG9wLkFwaSIsInJvdXRlIjoiL29yZGVycyJ9
using Shop.Api.Features.Orders;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Shop.Api.Tests.Features.Orders;

public class OrderEndpointsTests
{
    readonly InMemoryOrderStore _store = new();

    [Fact]
    public void Create_then_Get_returns_the_item()
    {
        var created = Assert.IsType<Created<Order>>(OrderEndpoints.Create(new("First"), _store).Result);

        var found = Assert.IsType<Ok<Order>>(OrderEndpoints.Get(created.Value!.Id, _store).Result);

        Assert.Equal("First", found.Value!.Name);
        Assert.Equal($"/orders/{created.Value.Id}", created.Location);
    }

    [Fact]
    public void Get_unknown_returns_NotFound() =>
        Assert.IsType<NotFound>(OrderEndpoints.Get(Guid.NewGuid(), _store).Result);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_without_name_returns_ValidationProblem(string name) =>
        Assert.IsType<ValidationProblem>(OrderEndpoints.Create(new(name), _store).Result);

    [Fact]
    public void List_returns_items_ordered_by_name()
    {
        _store.Add("b");
        _store.Add("a");

        var list = OrderEndpoints.List(_store).Value!;

        Assert.Equal(["a", "b"], list.Select(x => x.Name));
    }

    [Fact]
    public void Delete_removes_the_item_once()
    {
        var item = _store.Add("gone");

        Assert.IsType<NoContent>(OrderEndpoints.Delete(item.Id, _store).Result);
        Assert.IsType<NotFound>(OrderEndpoints.Delete(item.Id, _store).Result);
    }
}
