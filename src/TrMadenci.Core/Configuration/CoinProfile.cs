namespace TrMadenci.Core.Configuration;

public sealed record CoinProfile(
    string Id,
    string Name,
    string Ticker,
    string Network,
    string Algorithm,
    bool MiningEnabled,
    string? MiningBlockedReason = null);

public static class CoinProfileCatalog
{
    private static readonly IReadOnlyDictionary<string, CoinProfile> Profiles =
        new Dictionary<string, CoinProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["rvn"] = new(
                "rvn", "Ravencoin", "RVN", "Ravencoin Mainnet", "kawpow", true),
            ["etc"] = new(
                "etc", "Ethereum Classic", "ETC", "Ethereum Classic Mainnet", "etchash", false,
                "ETCHash correctness, performance and live-share qualification passed; soak qualification remains."),
            ["cfx"] = new(
                "cfx", "Conflux", "CFX", "Conflux Core Space Mainnet", "octopus", false,
                "Octopus GPU vector, performance and accepted live-share qualification remain incomplete."),
            ["xmr"] = new(
                "xmr", "Monero", "XMR", "Monero Mainnet", "randomx", false,
                "RandomX vector, full-memory context and pool protocol are implemented; " +
                "CPU worker/session, developer XMR wallet and live qualification remain incomplete.")
        };

    public static IReadOnlyCollection<CoinProfile> All => Profiles.Values.ToArray();

    public static CoinProfile GetRequired(string id) =>
        Profiles.TryGetValue(id, out var profile)
            ? profile
            : throw new ArgumentException($"Unknown coin profile '{id}'.");
}
