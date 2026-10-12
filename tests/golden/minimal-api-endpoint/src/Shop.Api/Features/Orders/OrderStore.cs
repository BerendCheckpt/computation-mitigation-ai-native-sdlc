// aico:generated template=minimal-api-endpoint@1.0.0 source=content/Store.cs.scriban params=eyJlbnRpdHkiOiJPcmRlciIsInByb2plY3QiOiJTaG9wLkFwaSIsInJvdXRlIjoiL29yZGVycyJ9
using System.Collections.Concurrent;

namespace Shop.Api.Features.Orders;

public interface IOrderStore
{
    IReadOnlyCollection<Order> List();
    Order? Find(Guid id);
    Order Add(string name);
    bool Remove(Guid id);
}

public sealed class InMemoryOrderStore : IOrderStore
{
    readonly ConcurrentDictionary<Guid, Order> _items = new();

    public IReadOnlyCollection<Order> List() => _items.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToList();

    public Order? Find(Guid id) => _items.GetValueOrDefault(id);

    public Order Add(string name)
    {
        var item = new Order(Guid.NewGuid(), name);
        _items[item.Id] = item;
        return item;
    }

    public bool Remove(Guid id) => _items.TryRemove(id, out _);
}
