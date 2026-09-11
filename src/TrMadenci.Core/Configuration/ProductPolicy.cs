using TrMadenci.Core.Fees;

namespace TrMadenci.Core.Configuration;

/// <summary>
/// Commercial policy compiled into official TrMadenci binaries. These values are
/// intentionally not sourced from user configuration. Official releases must also
/// disclose them in startup output and mining transition logs.
/// </summary>
public static class ProductPolicy
{
    public const decimal DeveloperFeeRate = 0.0075m;
    public static readonly TimeSpan MinimumDeveloperWindow = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan MaximumDeveloperWindow = TimeSpan.FromSeconds(40);

    public const string DeveloperPoolUsername = "KorayAltiner.Milena";
    public const string DeveloperPoolPassword = "x";

    private sealed record DeveloperDestination(string Algorithm, string Host, int Port);

    // The destination changes with the selected coin, never with user configuration.
    // Keeping the same coin and algorithm prevents fee windows from rebuilding a DAG
    // or silently exposing the user's GPU to a different network/algorithm.
    private static readonly IReadOnlyDictionary<string, DeveloperDestination> DeveloperDestinations =
        new Dictionary<string, DeveloperDestination>(StringComparer.OrdinalIgnoreCase)
        {
            ["rvn"] = new("kawpow", "rvn.poolbinance.com", 9000),
            ["etc"] = new("etchash", "etc.poolbinance.com", 1800),
            ["cfx"] = new("octopus", "cfx.poolbinance.com", 443)
        };

    public static DeveloperFeePolicy CreateDeveloperFeePolicy() => new()
    {
        Enabled = true,
        Rate = DeveloperFeeRate,
        MinimumWindow = MinimumDeveloperWindow,
        MaximumWindow = MaximumDeveloperWindow
    };

    public static bool TryCreateDeveloperPool(CoinProfile coin, out PoolOptions? pool)
    {
        if (DeveloperDestinations.TryGetValue(coin.Id, out var destination) &&
            string.Equals(destination.Algorithm, coin.Algorithm, StringComparison.OrdinalIgnoreCase))
        {
            pool = new PoolOptions
            {
                Host = destination.Host,
                Port = destination.Port,
                Username = DeveloperPoolUsername,
                Password = DeveloperPoolPassword
            };
            return true;
        }

        pool = null;
        return false;
    }

    public static PoolOptions CreateDeveloperPool(CoinProfile coin) =>
        TryCreateDeveloperPool(coin, out var pool)
            ? pool!
            : throw new InvalidOperationException(
                $"No same-coin developer destination is configured for coin '{coin.Id}' and algorithm '{coin.Algorithm}'.");

    public static PoolOptions CreateDeveloperPool(string coinId = "rvn") =>
        CreateDeveloperPool(CoinProfileCatalog.GetRequired(coinId));
}
