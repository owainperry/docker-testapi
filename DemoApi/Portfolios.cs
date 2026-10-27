using System.Collections.Concurrent;

namespace DemoApi;

public sealed record Portfolio(string Id, string Name, string Tenant);

/// <summary>
/// The service's own data. Which tenant each portfolio belongs to is also seeded into OpenFGA
/// (openfga/store.fga.yaml); creating a portfolio for real would write that tuple too.
/// </summary>
public static class Portfolios
{
    private static readonly ConcurrentDictionary<string, Portfolio> Data = new(StringComparer.Ordinal)
    {
        ["acme-1"] = new("acme-1", "Acme credit book", "d3275653-4f23-4862-843d-9b8052d5f9af"),
        ["other-1"] = new("other-1", "Other tenant's book", "00000000-0000-0000-0000-000000000001"),
    };

    public static bool TryGet(string id, out Portfolio portfolio) => Data.TryGetValue(id, out portfolio!);

    public static Portfolio Rename(Portfolio p, string name) => Data[p.Id] = p with { Name = name };
}
