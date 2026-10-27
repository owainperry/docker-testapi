namespace DemoApi;

public sealed record Portfolio(string Id, string Name, string Tenant);

public static class Portfolios
{
    private static readonly Dictionary<string, Portfolio> Data = new(StringComparer.Ordinal)
    {
        ["acme-1"] = new("acme-1", "Acme credit book", "d3275653-4f23-4862-843d-9b8052d5f9af"),
        ["other-1"] = new("other-1", "Other tenant's book", "00000000-0000-0000-0000-000000000001"),
    };

    public static bool TryGet(string id, out Portfolio portfolio) => Data.TryGetValue(id, out portfolio!);
}
