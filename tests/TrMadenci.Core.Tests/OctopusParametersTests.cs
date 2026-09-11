using TrMadenci.Core.Algorithms;

namespace TrMadenci.Core.Tests;

public sealed class OctopusParametersTests
{
    [Theory]
    [InlineData(0UL, 0)]
    [InlineData(524_287UL, 0)]
    [InlineData(524_288UL, 1)]
    [InlineData(225_968_128UL, 431)]
    public void Epoch_changes_every_two_to_the_nineteenth_blocks(
        ulong blockNumber,
        int expectedEpoch)
    {
        Assert.Equal(expectedEpoch, OctopusParameters.GetEpoch(blockNumber).EpochNumber);
    }

    [Theory]
    [InlineData(0, 16_776_896UL, 4_294_966_528UL)]
    [InlineData(1, 16_842_688UL, 4_311_744_256UL)]
    [InlineData(431, 45_022_144UL, 11_525_939_968UL)]
    [InlineData(722, 64_093_888UL, 16_408_116_992UL)]
    public void Cache_and_dataset_sizes_match_the_reference_miner(
        int epoch,
        ulong expectedCacheBytes,
        ulong expectedDatasetBytes)
    {
        Assert.Equal(expectedCacheBytes, OctopusParameters.CalculateLightCacheBytes(epoch));
        Assert.Equal(expectedDatasetBytes, OctopusParameters.CalculateFullDatasetBytes(epoch));
    }

    [Fact]
    public void Device_fit_includes_a_reserve_and_fails_closed()
    {
        const int epoch = 722;
        var required = OctopusParameters.RequiredDeviceMemoryBytes(epoch);

        Assert.False(OctopusParameters.CanFitDeviceMemory(epoch, 12UL << 30));
        Assert.True(OctopusParameters.CanFitDeviceMemory(epoch, required));
        Assert.False(OctopusParameters.CanFitDeviceMemory(epoch, required - 1));
    }

    [Fact]
    public void Negative_epoch_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OctopusParameters.CalculateFullDatasetBytes(-1));
    }
}
