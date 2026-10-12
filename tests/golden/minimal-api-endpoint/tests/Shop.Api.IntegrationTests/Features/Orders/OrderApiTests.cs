// aico:suggested template=minimal-api-endpoint@1.0.0 — suggested integration test; adapt it, then delete this line.
// Suggested integration test: add a Shop.Api.IntegrationTests project referencing Shop.Api and Microsoft.AspNetCore.Mvc.Testing.
// Adapt to the project's auth, database and fixtures before committing.
using System.Net;
using System.Net.Http.Json;
using Shop.Api.Features.Orders;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Shop.Api.IntegrationTests.Features.Orders;

public class OrderApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Post_then_get_roundtrips()
    {
        var post = await _client.PostAsJsonAsync("/orders", new CreateOrderRequest("Integration"));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        Assert.NotNull(post.Headers.Location);

        var created = await post.Content.ReadFromJsonAsync<Order>();
        var get = await _client.GetFromJsonAsync<Order>($"/orders/{created!.Id}");

        Assert.Equal("Integration", get!.Name);
    }

    [Fact]
    public async Task Post_without_name_is_rejected()
    {
        var post = await _client.PostAsJsonAsync("/orders", new CreateOrderRequest(""));
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Delete_unknown_returns_404()
    {
        var delete = await _client.DeleteAsync($"/orders/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }
}
