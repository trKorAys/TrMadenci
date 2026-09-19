using System.Text.Json;

namespace TrMadenci.Service.Mining;

internal sealed record EtcSoakQualificationResult(
    bool Passed,
    IReadOnlyList<string> Failures,
    TimeSpan? RequestedDuration = null,
    TimeSpan? WallClockDuration = null,
    TimeSpan? ActiveMiningTime = null,
    long AcceptedShares = 0,
    long AcceptedUserShares = 0,
    long RejectedShares = 0,
    long InvalidShares = 0,
    uint? MaximumTemperatureC = null,
    long MaximumRecoveries = 0,
    long StatusSamples = 0);

internal static class GpuSoakQualification
{
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromHours(24);
    private const double MinimumActiveRatio = 0.95;
    private const double MaximumRejectedRatio = 0.05;
    private const uint CriticalTemperatureC = 85;

    public static EtcSoakQualificationResult Evaluate(
        string summaryPath,
        string expectedCoin,
        string expectedAlgorithm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCoin);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAlgorithm);
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(summaryPath))
            return Failed($"The {expectedCoin} soak summary path is empty.");
        if (!File.Exists(summaryPath))
            return Failed($"The {expectedCoin} soak summary was not found: {Path.GetFullPath(summaryPath)}");

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(summaryPath));
            var root = document.RootElement;
            var schemaVersion = ReadInt64(root, "schemaVersion", failures);
            if (schemaVersion is not 1)
                failures.Add("Only soak summary schema version 1 is supported.");
            RequireString(root, "outcome", "completed", failures);
            RequireString(root, "coin", expectedCoin, failures);
            RequireString(root, "algorithm", expectedAlgorithm, failures);

            var requested = ReadTimeSpan(root, "requestedDuration", failures);
            var wallClock = ReadTimeSpan(root, "wallClockDuration", failures);
            var statusSamples = ReadInt64(root, "statusSamples", failures);
            var maximumTemperature = ReadUInt32(root, "maximumTemperatureC", failures);
            var maximumPower = ReadDouble(root, "maximumTotalPowerWatts", failures);
            var maximumRecoveries = ReadInt64(root, "maximumTotalRecoveries", failures);

            if (requested is { } requestedDuration && requestedDuration < MinimumDuration)
                failures.Add(
                    $"Requested duration {requestedDuration:c} is below the 24-hour {expectedCoin} gate.");
            if (requested is { } expectedWall && wallClock is { } observedWall && observedWall < expectedWall)
                failures.Add("Wall-clock duration is shorter than the requested soak duration.");
            if (requested is { } samplingDuration && statusSamples is { } samples)
            {
                var minimumSamples = (long)Math.Floor(samplingDuration.TotalSeconds / 20);
                if (samples < minimumSamples)
                    failures.Add($"Only {samples} status samples were recorded; at least {minimumSamples} are required.");
            }
            if (maximumTemperature is { } temperature && temperature >= CriticalTemperatureC)
                failures.Add($"Maximum GPU temperature {temperature} C reached the {CriticalTemperatureC} C critical limit.");
            if (maximumTemperature is 0)
                failures.Add("No positive GPU temperature observation was recorded.");
            if (maximumPower is not > 0)
                failures.Add("No positive GPU power observation was recorded.");
            if (!HasPositiveGpuMemoryObservation(root))
                failures.Add("No positive per-GPU VRAM observation was recorded.");
            if (root.TryGetProperty("finishedWhileManuallyPaused", out var finishedPaused) &&
                finishedPaused.ValueKind == JsonValueKind.True)
                failures.Add("The soak summary was finalized while mining was manually paused.");

            TimeSpan? activeMining = null;
            long accepted = 0;
            long acceptedUser = 0;
            long rejected = 0;
            long invalid = 0;
            if (!root.TryGetProperty("lastSnapshot", out var snapshot) ||
                snapshot.ValueKind != JsonValueKind.Object)
            {
                failures.Add("The final mining status snapshot is missing.");
            }
            else
            {
                activeMining = ReadTimeSpan(snapshot, "ActiveMiningTime", failures);
                accepted = ReadInt64(snapshot, "AcceptedShares", failures) ?? 0;
                acceptedUser = ReadInt64(snapshot, "AcceptedUserShares", failures) ?? 0;
                rejected = ReadInt64(snapshot, "RejectedShares", failures) ?? 0;
                invalid = ReadInt64(snapshot, "InvalidShares", failures) ?? 0;
                var energy = ReadDouble(snapshot, "SessionEnergyKwh", failures);
                if (energy is not > 0)
                    failures.Add("No positive session-energy measurement was recorded.");
                if (requested is { } target && activeMining is { } active &&
                    active < TimeSpan.FromTicks((long)(target.Ticks * MinimumActiveRatio)))
                    failures.Add(
                        $"Active mining time {active:c} is below {MinimumActiveRatio:P0} of the requested duration.");
                if (accepted < 1)
                    failures.Add($"No pool-accepted {expectedCoin} share was recorded.");
                if (acceptedUser < 1)
                    failures.Add(
                        $"No user-beneficiary {expectedCoin} share was accepted during the soak.");
                if (acceptedUser > accepted)
                    failures.Add("User-beneficiary accepted shares exceed the total accepted-share count.");
                if (invalid != 0)
                    failures.Add($"The soak recorded {invalid} locally invalid share(s).");
                var submitted = accepted + rejected;
                if (submitted > 0 && rejected / (double)submitted > MaximumRejectedRatio)
                    failures.Add(
                        $"Rejected-share ratio {rejected / (double)submitted:P2} exceeds {MaximumRejectedRatio:P0}.");
                ValidateFinalGpuHealth(snapshot, failures);
            }

            return new EtcSoakQualificationResult(
                failures.Count == 0,
                failures,
                requested,
                wallClock,
                activeMining,
                accepted,
                acceptedUser,
                rejected,
                invalid,
                maximumTemperature,
                maximumRecoveries ?? 0,
                statusSamples ?? 0);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return Failed($"The {expectedCoin} soak summary could not be read: {exception.Message}");
        }
    }

    private static void ValidateFinalGpuHealth(JsonElement snapshot, List<string> failures)
    {
        if (!snapshot.TryGetProperty("Gpus", out var gpus) || gpus.ValueKind != JsonValueKind.Array ||
            gpus.GetArrayLength() == 0)
        {
            failures.Add("The final snapshot contains no GPU health records.");
            return;
        }

        foreach (var gpu in gpus.EnumerateArray())
        {
            if (!gpu.TryGetProperty("WorkerPhase", out var phase) || phase.ValueKind != JsonValueKind.String)
            {
                failures.Add("A final GPU worker phase is missing.");
                continue;
            }
            if (phase.GetString() is "Faulted" or "Recovering")
                failures.Add($"A GPU ended the soak in the {phase.GetString()} phase.");
        }
    }

    private static bool HasPositiveGpuMemoryObservation(JsonElement root)
    {
        if (!root.TryGetProperty("maximumGpuMemoryUsedBytes", out var memory) ||
            memory.ValueKind != JsonValueKind.Object)
            return false;
        return memory.EnumerateObject().Any(property =>
            property.Value.ValueKind == JsonValueKind.Number &&
            property.Value.TryGetUInt64(out var bytes) && bytes > 0);
    }

    private static void RequireString(
        JsonElement element,
        string propertyName,
        string expected,
        List<string> failures)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            !string.Equals(property.GetString(), expected, StringComparison.OrdinalIgnoreCase))
            failures.Add($"Expected {propertyName}='{expected}'.");
    }

    private static TimeSpan? ReadTimeSpan(
        JsonElement element,
        string propertyName,
        List<string> failures)
    {
        if (element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            TimeSpan.TryParse(property.GetString(), out var value))
            return value;
        failures.Add($"A valid {propertyName} duration is required.");
        return null;
    }

    private static long? ReadInt64(
        JsonElement element,
        string propertyName,
        List<string> failures)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value))
            return value;
        failures.Add($"A valid {propertyName} integer is required.");
        return null;
    }

    private static uint? ReadUInt32(
        JsonElement element,
        string propertyName,
        List<string> failures)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.TryGetUInt32(out var value))
            return value;
        failures.Add($"A valid {propertyName} temperature is required.");
        return null;
    }

    private static double? ReadDouble(
        JsonElement element,
        string propertyName,
        List<string> failures)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value))
            return value;
        failures.Add($"A valid {propertyName} number is required.");
        return null;
    }

    private static EtcSoakQualificationResult Failed(string failure) =>
        new(false, [failure]);
}

internal static class EtcSoakQualification
{
    public static EtcSoakQualificationResult Evaluate(string summaryPath) =>
        GpuSoakQualification.Evaluate(summaryPath, "ETC", "ETCHASH");
}

internal static class CfxSoakQualification
{
    public static EtcSoakQualificationResult Evaluate(string summaryPath) =>
        GpuSoakQualification.Evaluate(summaryPath, "CFX", "OCTOPUS");
}
