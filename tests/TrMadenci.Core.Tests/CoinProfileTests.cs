using System.Text.Json;
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

    [Fact]
    public void Legacy_gpu_indexes_remain_valid_with_auto_backend()
    {
        var options = ValidOptions() with { GpuDevices = [2, 0] };

        options.Validate();

        Assert.Equal(ComputeBackendMode.Auto, options.ComputeBackend);
        Assert.Equal([2, 0], options.GetCudaDeviceIndexes());
    }

    [Fact]
    public void Explicit_cuda_device_ids_are_validated_and_resolved()
    {
        var options = ValidOptions() with
        {
            ComputeBackend = ComputeBackendMode.Cuda,
            ComputeDevices = ["cuda:3", "CUDA:1"]
        };

        options.Validate();

        Assert.Equal([3, 1], options.GetCudaDeviceIndexes());
    }

    [Fact]
    public void Opencl_device_ids_preserve_platform_and_reject_legacy_indexes()
    {
        var options = ValidOptions() with
        {
            ComputeBackend = ComputeBackendMode.OpenCl,
            ComputeDevices = ["opencl:1:3"]
        };
        options.Validate();
        Assert.Empty(options.GetCudaDeviceIndexes());

        var conflicting = options with { GpuDevices = [0] };
        Assert.Throws<ArgumentException>(conflicting.Validate);
    }

    [Theory]
    [InlineData("auto", "cuda:0")]
    [InlineData("cuda", "opencl:0:0")]
    [InlineData("openCl", "cuda:0")]
    [InlineData("cuda", "cuda:-1")]
    public void Conflicting_or_invalid_compute_selection_is_rejected(string backend, string device)
    {
        var json = $$"""
            {
              "coin": "rvn",
              "algorithm": "kawpow",
              "computeBackend": "{{backend}}",
              "computeDevices": ["{{device}}"],
              "pool": { "host": "pool", "port": 1, "username": "worker" }
            }
            """;
        var options = JsonSerializer.Deserialize<MinerOptions>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!;

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Numeric_compute_backend_is_rejected_by_json_contract()
    {
        const string json = """
            {
              "coin": "rvn",
              "algorithm": "kawpow",
              "computeBackend": 1,
              "pool": { "host": "pool", "port": 1, "username": "worker" }
            }
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MinerOptions>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }));
    }

    private static MinerOptions ValidOptions() => new()
    {
        Coin = "rvn",
        Algorithm = "kawpow",
        Pool = new PoolOptions { Host = "pool", Port = 1, Username = "worker" }
    };
}
