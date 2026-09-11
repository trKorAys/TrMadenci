namespace TrMadenci.Core.Algorithms;

/// <summary>
/// Conflux Octopus epoch and memory sizing rules. These values follow the
/// Conflux reference miner; the dataset is deliberately much larger than the
/// Ethash-family DAG and must be checked before a GPU allocation is attempted.
/// </summary>
public static class OctopusParameters
{
    public const ulong EpochLength = 1UL << 19;
    public const ulong LightCacheInitialBytes = 1UL << 24;
    public const ulong LightCacheGrowthBytes = 1UL << 16;
    public const ulong LightCacheItemBytes = 64;
    public const ulong FullDatasetInitialBytes = 1UL << 32;
    public const ulong FullDatasetGrowthBytes = 1UL << 24;
    public const ulong FullDatasetItemBytes = 256;

    // CUDA context, search results and driver allocations need room in addition
    // to the DAG. The final backend may raise this after hardware qualification.
    public const ulong DefaultDeviceReserveBytes = 512UL << 20;

    public static OctopusEpoch GetEpoch(ulong blockNumber)
    {
        var epochNumber = blockNumber / EpochLength;
        if (epochNumber > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(blockNumber));

        return new OctopusEpoch(
            (int)epochNumber,
            CalculateLightCacheBytes((int)epochNumber),
            CalculateFullDatasetBytes((int)epochNumber));
    }

    public static ulong CalculateLightCacheBytes(int epochNumber) => CalculatePrimeSizedBytes(
        epochNumber,
        LightCacheInitialBytes,
        LightCacheGrowthBytes,
        LightCacheItemBytes);

    public static ulong CalculateFullDatasetBytes(int epochNumber) => CalculatePrimeSizedBytes(
        epochNumber,
        FullDatasetInitialBytes,
        FullDatasetGrowthBytes,
        FullDatasetItemBytes);

    public static ulong RequiredDeviceMemoryBytes(
        int epochNumber,
        ulong reserveBytes = DefaultDeviceReserveBytes) =>
        checked(CalculateFullDatasetBytes(epochNumber) + reserveBytes);

    public static bool CanFitDeviceMemory(
        int epochNumber,
        ulong totalDeviceMemoryBytes,
        ulong reserveBytes = DefaultDeviceReserveBytes) =>
        totalDeviceMemoryBytes >= RequiredDeviceMemoryBytes(epochNumber, reserveBytes);

    private static ulong CalculatePrimeSizedBytes(
        int epochNumber,
        ulong initialBytes,
        ulong growthBytes,
        ulong itemBytes)
    {
        if (epochNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(epochNumber));

        var size = checked(initialBytes + growthBytes * (ulong)epochNumber - itemBytes);
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

public sealed record OctopusEpoch(
    int EpochNumber,
    ulong LightCacheBytes,
    ulong FullDatasetBytes);
