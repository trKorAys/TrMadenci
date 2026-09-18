using TrMadenci.Core.Fees;
using TrMadenci.NativeBridge;
using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class MiningStatusTransportTests
{
    [Fact]
    public void Supervisor_status_transport_round_trips_every_dashboard_field()
    {
        var expected = new MiningStatusSnapshot(
            DateTimeOffset.Parse("2026-09-13T01:00:00+03:00"),
            TimeSpan.FromHours(2),
            TimeSpan.FromMinutes(110),
            "Ethereum Classic",
            "ETC",
            "Ethereum Classic Mainnet",
            "ETCHASH",
            "etc.poolbinance.com:443",
            MiningBeneficiary.User,
            15_300_000,
            15_100_000,
            1_000_000_000,
            14,
            0,
            0,
            0.258,
            128.5,
            [
                new GpuMiningStatus(
                    0,
                    false,
                    15_300_000,
                    new GpuTelemetry(0, 69, 67, 130, 100, 52, 6UL << 30, 12UL << 30, 1905, 7301),
                    "cuda:0",
                    ComputeWorkerPhase.Hashing)
            ],
            false,
            14,
            0);

        var encoded = MiningStatusTransport.Encode(expected);
        Assert.True(MiningStatusTransport.TryDecode(encoded, out var actual));

        Assert.NotNull(actual);
        Assert.Equal(expected with { Gpus = actual.Gpus }, actual);
        Assert.Equal(expected.Gpus, actual.Gpus);
        Assert.Equal(expected.Gpus[0], actual!.Gpus[0]);
    }

    [Fact]
    public void Non_status_or_corrupt_transport_lines_are_rejected()
    {
        Assert.False(MiningStatusTransport.TryDecode("ordinary log", out _));
        Assert.False(MiningStatusTransport.TryDecode(MiningStatusTransport.Prefix + "not-base64", out _));
    }

    [Fact]
    public void Console_keys_use_s_for_start_and_d_for_status()
    {
        Assert.Equal(MiningControlCommand.Pause, ConsoleMiningKeyListener.MapKey(ConsoleKey.P));
        Assert.Equal(MiningControlCommand.Resume, ConsoleMiningKeyListener.MapKey(ConsoleKey.S));
        Assert.Equal(MiningControlCommand.Resume, ConsoleMiningKeyListener.MapKey(ConsoleKey.R));
        Assert.Equal(MiningControlCommand.Status, ConsoleMiningKeyListener.MapKey(ConsoleKey.D));
        Assert.Null(ConsoleMiningKeyListener.MapKey(ConsoleKey.X));
    }
}
