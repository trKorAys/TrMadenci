namespace TrMadenci.Core.Algorithms;

/// <summary>
/// Ethereum Classic mainnet ECIP-1099 epoch rules. ETCHash deliberately uses
/// the reduced epoch for cache/DAG sizing and the unreduced Ethash epoch for
/// the seed, so these values must never be collapsed into a single number.
/// </summary>
public static class EtcHashParameters
{
    public const int ActivationBlock = 11_700_000;
    public const int LegacyEpochLength = 30_000;
    public const int EpochLength = 60_000;

    private const ulong LightCacheInitialBytes = 1UL << 24;
    private const ulong LightCacheGrowthBytes = 1UL << 17;
    private const ulong LightCacheItemBytes = 64;
    private const ulong FullDatasetInitialBytes = 1UL << 30;
    private const ulong FullDatasetGrowthBytes = 1UL << 23;
    private const ulong FullDatasetItemBytes = 128;

    public static EtcHashEpoch GetEpoch(int blockNumber)
    {
        if (blockNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(blockNumber));

        var epochLength = blockNumber < ActivationBlock ? LegacyEpochLength : EpochLength;
        var datasetEpoch = blockNumber / epochLength;
        var seedEpoch = checked(datasetEpoch * (epochLength / LegacyEpochLength));
        return new EtcHashEpoch(
            datasetEpoch,
            seedEpoch,
            CalculateLightCacheBytes(datasetEpoch),
            CalculateFullDatasetBytes(datasetEpoch));
    }

    public static int GetDatasetEpochFromSeedEpoch(int seedEpoch)
    {
        if (seedEpoch < 0)
            throw new ArgumentOutOfRangeException(nameof(seedEpoch));
        var activationSeedEpoch = ActivationBlock / LegacyEpochLength;
        if (seedEpoch < activationSeedEpoch)
            return seedEpoch;
        if ((seedEpoch & 1) != 0)
            throw new ArgumentException(
                "Post-ECIP-1099 ETC jobs must use an even, non-reused seed epoch.",
                nameof(seedEpoch));
        return seedEpoch / 2;
    }

    public static int GetRepresentativeBlock(int seedEpoch)
    {
        var datasetEpoch = GetDatasetEpochFromSeedEpoch(seedEpoch);
        var activationSeedEpoch = ActivationBlock / LegacyEpochLength;
        var epochLength = seedEpoch < activationSeedEpoch ? LegacyEpochLength : EpochLength;
        return checked(datasetEpoch * epochLength);
    }

    public static ulong CalculateLightCacheBytes(int datasetEpoch) => CalculatePrimeSizedBytes(
        datasetEpoch,
        LightCacheInitialBytes,
        LightCacheGrowthBytes,
        LightCacheItemBytes);

    public static ulong CalculateFullDatasetBytes(int datasetEpoch) => CalculatePrimeSizedBytes(
        datasetEpoch,
        FullDatasetInitialBytes,
        FullDatasetGrowthBytes,
        FullDatasetItemBytes);

    private static ulong CalculatePrimeSizedBytes(
        int epoch,
        ulong initialBytes,
        ulong growthBytes,
        ulong itemBytes)
    {
        if (epoch < 0)
            throw new ArgumentOutOfRangeException(nameof(epoch));

        var size = checked(initialBytes + growthBytes * (ulong)epoch - itemBytes);
        while (!IsPrime(size / itemBytes))
            size -= 2 * itemBytes;
        return size;
    }

    private static bool IsPrime(ulong value)
    {
        if (value < 2)
            return false;
        if ((value & 1) == 0)
            return value == 2;
        for (ulong divisor = 3; divisor <= value / divisor; divisor += 2)
        {
            if (value % divisor == 0)
                return false;
        }
        return true;
    }
}

public sealed record EtcHashEpoch(
    int DatasetEpoch,
    int SeedEpoch,
    ulong LightCacheBytes,
    ulong FullDatasetBytes);
