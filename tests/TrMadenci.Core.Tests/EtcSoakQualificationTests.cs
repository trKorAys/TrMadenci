using System.Text.Json;
using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class EtcSoakQualificationTests
{
    [Fact]
    public void Complete_healthy_24_hour_evidence_passes()
    {
        var path = WriteSummary(CreateSummary());
        try
        {
            var result = EtcSoakQualification.Evaluate(path);

            Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures));
            Assert.Empty(result.Failures);
            Assert.Equal(2, result.AcceptedShares);
            Assert.Equal(1, result.AcceptedUserShares);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Short_or_unhealthy_evidence_reports_every_failed_gate()
    {
        var path = WriteSummary(CreateSummary(
            requestedDuration: TimeSpan.FromHours(1),
            wallClockDuration: TimeSpan.FromMinutes(59),
            activeMiningTime: TimeSpan.FromMinutes(20),
            acceptedShares: 1,
            acceptedUserShares: 0,
            rejectedShares: 1,
            invalidShares: 2,
            maximumTemperatureC: 85,
            workerPhase: "Faulted",
            statusSamples: 1));
        try
        {
            var result = EtcSoakQualification.Evaluate(path);

            Assert.False(result.Passed);
            Assert.Contains(result.Failures, failure => failure.Contains("24-hour", StringComparison.Ordinal));
            Assert.Contains(result.Failures, failure => failure.Contains("user-beneficiary", StringComparison.Ordinal));
            Assert.Contains(result.Failures, failure => failure.Contains("locally invalid", StringComparison.Ordinal));
            Assert.Contains(result.Failures, failure => failure.Contains("Rejected-share ratio", StringComparison.Ordinal));
            Assert.Contains(result.Failures, failure => failure.Contains("critical limit", StringComparison.Ordinal));
            Assert.Contains(result.Failures, failure => failure.Contains("Faulted", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static object CreateSummary(
        TimeSpan? requestedDuration = null,
        TimeSpan? wallClockDuration = null,
        TimeSpan? activeMiningTime = null,
        long acceptedShares = 2,
        long acceptedUserShares = 1,
        long rejectedShares = 0,
        long invalidShares = 0,
        uint maximumTemperatureC = 78,
        string workerPhase = "Hashing",
        long statusSamples = 5_000) => new
        {
            schemaVersion = 1,
            coin = "ETC",
            algorithm = "ETCHASH",
            outcome = "completed",
            requestedDuration = requestedDuration ?? TimeSpan.FromHours(24),
            wallClockDuration = wallClockDuration ?? TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1),
            statusSamples,
            maximumTemperatureC,
            maximumTotalPowerWatts = 140.0,
            maximumTotalRecoveries = 1,
            maximumGpuMemoryUsedBytes = new Dictionary<string, ulong> { ["cuda:0"] = 6UL << 30 },
            manualPauseCount = 0,
            manualPausedDuration = TimeSpan.Zero,
            finishedWhileManuallyPaused = false,
            lastSnapshot = new
            {
                ActiveMiningTime = activeMiningTime ?? TimeSpan.FromHours(23),
                AcceptedShares = acceptedShares,
                AcceptedUserShares = acceptedUserShares,
                AcceptedDeveloperShares = acceptedShares - acceptedUserShares,
                RejectedShares = rejectedShares,
                InvalidShares = invalidShares,
                SessionEnergyKwh = 3.1,
                Gpus = new[] { new { WorkerPhase = workerPhase } }
            }
        };

    private static string WriteSummary(object summary)
    {
        var path = Path.Combine(Path.GetTempPath(), $"TrMadenci-etc-soak-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(summary));
        return path;
    }
}
