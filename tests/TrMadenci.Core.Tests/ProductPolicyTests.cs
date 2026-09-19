using System.Text.Json;
using TrMadenci.Core.Configuration;

namespace TrMadenci.Core.Tests;

public sealed class ProductPolicyTests
{
    [Fact]
    public void Official_developer_fee_is_fixed_and_enabled()
    {
        var policy = ProductPolicy.CreateDeveloperFeePolicy();

        Assert.True(policy.Enabled);
        Assert.Equal(0.0075m, policy.Rate);
        Assert.Equal(TimeSpan.FromSeconds(20), policy.MinimumWindow);
        Assert.Equal(TimeSpan.FromSeconds(40), policy.MaximumWindow);
        Assert.Equal("KorayAltiner.Milena", ProductPolicy.CreateDeveloperPool().Username);
    }

    [Theory]
    [InlineData("rvn", "kawpow", "rvn.poolbinance.com", 9000)]
    [InlineData("etc", "etchash", "etc.poolbinance.com", 1800)]
    [InlineData("cfx", "octopus", "cfx.poolbinance.com", 443)]
    public void Developer_destination_is_bound_to_the_selected_coin_and_algorithm(
        string coinId, string algorithm, string host, int port)
    {
        var coin = CoinProfileCatalog.GetRequired(coinId);
        var pool = ProductPolicy.CreateDeveloperPool(coin);

        Assert.Equal(algorithm, coin.Algorithm);
        Assert.Equal(host, pool.Host);
        Assert.Equal(port, pool.Port);
        Assert.EndsWith(".poolbinance.com", pool.Host, StringComparison.Ordinal);
        Assert.Equal(ProductPolicy.DeveloperPoolUsername, pool.Username);
    }

    [Fact]
    public void User_pool_does_not_redirect_the_embedded_developer_destination()
    {
        var options = new MinerOptions
        {
            Coin = "rvn",
            Algorithm = "kawpow",
            Pool = new PoolOptions
            {
                Host = "rvn.example-user-pool.test",
                Port = 1234,
                Username = "user.worker"
            }
        };

        options.Validate();
        var developerPool = ProductPolicy.CreateDeveloperPool(CoinProfileCatalog.GetRequired(options.Coin));

        Assert.Equal("rvn.example-user-pool.test", options.Pool.Host);
        Assert.Equal("rvn.poolbinance.com", developerPool.Host);
        Assert.NotEqual(options.Pool.Username, developerPool.Username);
    }

    [Fact]
    public void Every_configured_developer_destination_uses_the_shared_embedded_worker()
    {
        foreach (var coin in CoinProfileCatalog.All)
        {
            if (!ProductPolicy.TryCreateDeveloperPool(coin, out var pool))
                continue;

            Assert.Equal(ProductPolicy.DeveloperPoolUsername, pool!.Username);
            Assert.EndsWith(".poolbinance.com", pool.Host, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Monero_cannot_start_without_an_explicit_embedded_developer_wallet()
    {
        var monero = CoinProfileCatalog.GetRequired("xmr");

        Assert.False(ProductPolicy.TryCreateDeveloperPool(monero, out var pool));
        Assert.Null(pool);
        Assert.Throws<InvalidOperationException>(() => ProductPolicy.CreateDeveloperPool(monero));
    }

    [Fact]
    public void Configuration_cannot_override_developer_fee()
    {
        const string json = """
            {
              "algorithm": "kawpow",
              "pool": { "host": "pool", "port": 1, "username": "worker" },
              "developerFee": { "enabled": false, "rate": 0 }
            }
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MinerOptions>(json));
    }
}
