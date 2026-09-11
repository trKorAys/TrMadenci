using TrMadenci.Core.Configuration;

namespace TrMadenci.Core.Tests;

public sealed class CoinProfileTests
{
    [Fact]
    public void Ravencoin_profile_is_enabled_and_has_an_embedded_fee_destination()
    {
        var profile = CoinProfileCatalog.GetRequired("RVN");

        Assert.True(profile.MiningEnabled);
        Assert.Equal("kawpow", profile.Algorithm);
        Assert.Equal("KorayAltiner.Milena", ProductPolicy.CreateDeveloperPool(profile.Id).Username);
    }

    [Fact]
    public void Binance_first_future_profiles_are_gated_until_their_engines_are_ready()
    {
        var etc = CoinProfileCatalog.GetRequired("etc");
        var cfx = CoinProfileCatalog.GetRequired("cfx");

        Assert.False(etc.MiningEnabled);
        Assert.Equal("etchash", etc.Algorithm);
        Assert.Equal("etc.poolbinance.com", ProductPolicy.CreateDeveloperPool(etc).Host);
        Assert.False(cfx.MiningEnabled);
        Assert.Equal("octopus", cfx.Algorithm);
        Assert.Equal("cfx.poolbinance.com", ProductPolicy.CreateDeveloperPool(cfx).Host);
    }

    [Fact]
    public void Configuration_algorithm_must_match_the_coin_profile()
    {
        var options = new MinerOptions
        {
            Coin = "etc",
            Algorithm = "kawpow",
            Pool = new PoolOptions { Host = "pool", Port = 1, Username = "worker" }
        };

        Assert.Throws<ArgumentException>(options.Validate);
    }
}
