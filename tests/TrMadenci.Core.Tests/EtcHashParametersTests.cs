using TrMadenci.Core.Algorithms;

namespace TrMadenci.Core.Tests;

public sealed class EtcHashParametersTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(11_699_999, 389, 389)]
    [InlineData(11_700_000, 195, 390)]
    [InlineData(11_759_999, 195, 390)]
    [InlineData(11_760_000, 196, 392)]
    public void Mainnet_epoch_and_seed_follow_ecip_1099(
        int blockNumber, int datasetEpoch, int seedEpoch)
    {
        var result = EtcHashParameters.GetEpoch(blockNumber);

        Assert.Equal(datasetEpoch, result.DatasetEpoch);
        Assert.Equal(seedEpoch, result.SeedEpoch);
    }

    [Fact]
    public void Epoch_four_consensus_sizes_match_ecip_1099_data_without_file_header()
    {
        // The published research file sizes are 8 bytes larger because geth's
        // on-disk cache/DAG format prefixes a dump magic header.
        Assert.Equal(17_301_056UL, EtcHashParameters.CalculateLightCacheBytes(4));
        Assert.Equal(1_107_293_056UL, EtcHashParameters.CalculateFullDatasetBytes(4));
    }

    [Fact]
    public void Invalid_block_and_epoch_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EtcHashParameters.GetEpoch(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EtcHashParameters.CalculateLightCacheBytes(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EtcHashParameters.CalculateFullDatasetBytes(-1));
    }

    [Theory]
    [InlineData(389, 389)]
    [InlineData(390, 195)]
    [InlineData(800, 400)]
    public void Seed_epoch_maps_to_the_correct_dataset_epoch(int seedEpoch, int datasetEpoch)
    {
        Assert.Equal(datasetEpoch, EtcHashParameters.GetDatasetEpochFromSeedEpoch(seedEpoch));
    }

    [Fact]
    public void Odd_post_fork_seed_epoch_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => EtcHashParameters.GetDatasetEpochFromSeedEpoch(391));
    }

    [Theory]
    [InlineData(194, 5_820_000)]
    [InlineData(390, 11_700_000)]
    [InlineData(844, 25_320_000)]
    public void Representative_block_preserves_seed_and_dataset_epoch(int seedEpoch, int expectedBlock)
    {
        var block = EtcHashParameters.GetRepresentativeBlock(seedEpoch);
        var epoch = EtcHashParameters.GetEpoch(block);

        Assert.Equal(expectedBlock, block);
        Assert.Equal(seedEpoch, epoch.SeedEpoch);
        Assert.Equal(EtcHashParameters.GetDatasetEpochFromSeedEpoch(seedEpoch), epoch.DatasetEpoch);
    }
}
