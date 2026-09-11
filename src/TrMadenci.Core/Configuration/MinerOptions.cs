using System.Text.Json.Serialization;

namespace TrMadenci.Core.Configuration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MinerOptions
{
    public string Coin { get; init; } = "rvn";
    public string Algorithm { get; init; } = "kawpow";
    public PoolOptions Pool { get; init; } = new();
    public PoolOptions? FailoverPool { get; init; }
    public int[] GpuDevices { get; init; } = [];

    public void Validate()
    {
        var profile = CoinProfileCatalog.GetRequired(Coin);
        if (!string.Equals(Algorithm, profile.Algorithm, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Coin '{profile.Ticker}' requires algorithm '{profile.Algorithm}'.");

        Pool.Validate(nameof(Pool));
        FailoverPool?.Validate(nameof(FailoverPool));
        if (GpuDevices.Distinct().Count() != GpuDevices.Length || GpuDevices.Any(index => index < 0))
            throw new ArgumentException("GPU device indexes must be unique and non-negative.");
    }
}

public sealed record PoolOptions
{
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Username { get; init; } = "";
    public string Password { get; init; } = "x";

    public void Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(Host))
            throw new ArgumentException($"{name} host is required.");
        if (Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException($"{name}.Port");
        if (string.IsNullOrWhiteSpace(Username))
            throw new ArgumentException($"{name} username/worker is required.");
    }
}
