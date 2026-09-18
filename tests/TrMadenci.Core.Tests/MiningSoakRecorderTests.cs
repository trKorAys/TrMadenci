using System.Text.Json;
using TrMadenci.Core.Configuration;
using TrMadenci.Core.Fees;
using TrMadenci.NativeBridge;
using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class MiningSoakRecorderTests
{
    [Fact]
    public void Recorder_writes_coin_neutral_evidence_and_health_summary()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "TrMadenciSoakRecorderTests", Guid.NewGuid().ToString("N"));
        try
        {
            string eventsPath;
            string summaryPath;
            var coin = CoinProfileCatalog.GetRequired("rvn");
            var pauseController = new MiningPauseController();
            pauseController.Pause();
            using (var recorder = new MiningSoakRecorder(
                coin,
                TimeSpan.FromHours(24),
                directory,
                pauseController))
            {
                recorder.RecordEvent("fixture event");
                recorder.RecordStatus(CreateSnapshot(coin, 64, 100, recoveryCount: 1));
                recorder.RecordStatus(CreateSnapshot(coin, 72, 125, recoveryCount: 3));
                recorder.Complete("completed");
                eventsPath = recorder.EventsPath;
                summaryPath = recorder.SummaryPath;
            }

            Assert.StartsWith("rvn-soak-", Path.GetFileName(eventsPath), StringComparison.Ordinal);
            Assert.Equal(5, File.ReadAllLines(eventsPath).Length);
            using var summary = JsonDocument.Parse(File.ReadAllText(summaryPath));
            var root = summary.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("RVN", root.GetProperty("coin").GetString());
            Assert.Equal("KAWPOW", root.GetProperty("algorithm").GetString());
            Assert.Equal("completed", root.GetProperty("outcome").GetString());
            Assert.Equal(2, root.GetProperty("statusSamples").GetInt64());
            Assert.Equal(72u, root.GetProperty("maximumTemperatureC").GetUInt32());
            Assert.Equal(125, root.GetProperty("maximumTotalPowerWatts").GetDouble());
            Assert.Equal(3, root.GetProperty("maximumTotalRecoveries").GetInt64());
            Assert.Equal(4UL * 1024 * 1024 * 1024, root
                .GetProperty("maximumGpuMemoryUsedBytes")
                .GetProperty("cuda:0")
                .GetUInt64());
            Assert.Equal(1, root.GetProperty("manualPauseCount").GetInt32());
            Assert.True(root.GetProperty("finishedWhileManuallyPaused").GetBoolean());
            Assert.Equal(1, root.GetProperty("lastSnapshot")
                .GetProperty("AcceptedShares").GetInt64());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static MiningStatusSnapshot CreateSnapshot(
        CoinProfile coin,
        uint temperature,
        double power,
        long recoveryCount)
    {
        var telemetry = new GpuTelemetry(
            0,
            temperature,
            50,
            power,
            100,
            50,
            4UL * 1024 * 1024 * 1024,
            12UL * 1024 * 1024 * 1024,
            1800,
            7000);
        return new MiningStatusSnapshot(
            DateTimeOffset.Now,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(50),
            coin.Name,
            coin.Ticker,
            coin.Network,
            coin.Algorithm.ToUpperInvariant(),
            "pool.example:9000",
            MiningBeneficiary.User,
            12_000_000,
            11_000_000,
            600_000_000,
            1,
            0,
            0,
            0.002,
            power,
            [
                new GpuMiningStatus(
                    0,
                    false,
                    12_000_000,
                    telemetry,
                    "cuda:0",
                    ComputeWorkerPhase.Hashing,
                    recoveryCount)
            ]);
    }
}
