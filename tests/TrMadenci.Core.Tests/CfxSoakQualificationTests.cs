using System.Text.Json;
using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class CfxSoakQualificationTests
{
    [Fact]
    public void Complete_healthy_24_hour_cfx_evidence_passes()
    {
        var path = WriteSummary("CFX", "OCTOPUS");
        try
        {
            var result = CfxSoakQualification.Evaluate(path);

            Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures));
            Assert.Equal(3, result.AcceptedShares);
            Assert.Equal(2, result.AcceptedUserShares);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("ETC", "OCTOPUS")]
    [InlineData("CFX", "ETCHASH")]
    public void Wrong_coin_or_algorithm_is_rejected(string coin, string algorithm)
    {
        var path = WriteSummary(coin, algorithm);
        try
        {
            var result = CfxSoakQualification.Evaluate(path);

            Assert.False(result.Passed);
            Assert.Contains(result.Failures, failure =>
                failure.Contains("Expected", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteSummary(string coin, string algorithm)
    {
        var summary = new
        {
            schemaVersion = 1,
            coin,
            algorithm,
            outcome = "completed",
            requestedDuration = TimeSpan.FromHours(24),
            wallClockDuration = TimeSpan.FromHours(24) + TimeSpan.FromMinutes(2),
            statusSamples = 5_000,
            maximumTemperatureC = 76,
            maximumTotalPowerWatts = 145.0,
            maximumTotalRecoveries = 0,
            maximumGpuMemoryUsedBytes = new Dictionary<string, ulong> { ["cuda:0"] = 10UL << 30 },
            manualPauseCount = 0,
            manualPausedDuration = TimeSpan.Zero,
            finishedWhileManuallyPaused = false,
            lastSnapshot = new
            {
                ActiveMiningTime = TimeSpan.FromHours(23),
                AcceptedShares = 3,
                AcceptedUserShares = 2,
                AcceptedDeveloperShares = 1,
                RejectedShares = 0,
                InvalidShares = 0,
                SessionEnergyKwh = 3.2,
                Gpus = new[] { new { WorkerPhase = "Hashing" } }
            }
        };
        var path = Path.Combine(Path.GetTempPath(), $"TrMadenci-cfx-soak-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(summary));
        return path;
    }
}
