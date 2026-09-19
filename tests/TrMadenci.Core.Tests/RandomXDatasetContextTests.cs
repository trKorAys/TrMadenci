using TrMadenci.NativeBridge;

namespace TrMadenci.Core.Tests;

public sealed class RandomXDatasetContextTests
{
    [Fact]
    public void CreateRejectsMissingKeyBeforeAllocatingDataset()
    {
        Assert.Throws<ArgumentNullException>(() =>
            NativeDiagnostics.CreateRandomXDataset(null!, 1, 1));
        Assert.Throws<ArgumentException>(() =>
            NativeDiagnostics.CreateRandomXDataset([], 1, 1));
    }

    [Fact]
    public void CreateRejectsZeroVirtualMachinesBeforeAllocatingDataset()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeDiagnostics.CreateRandomXDataset([1], 0, 1));
    }

    [Fact]
    public void CreateRejectsZeroInitializationThreadsBeforeAllocatingDataset()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeDiagnostics.CreateRandomXDataset([1], 1, 0));
    }
}
