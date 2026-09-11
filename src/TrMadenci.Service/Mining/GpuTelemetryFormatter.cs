using TrMadenci.NativeBridge;

namespace TrMadenci.Service.Mining;

internal static class GpuTelemetryFormatter
{
    private const double BytesPerGibibyte = 1024d * 1024d * 1024d;

    public static string Format(int deviceIndex, double hashesPerSecond, GpuTelemetry? telemetry)
    {
        var fields = new List<string>
        {
            $"GPU{deviceIndex} {hashesPerSecond / 1_000_000:F2} MH/s"
        };

        if (telemetry is null)
        {
            fields.Add("telemetry n/a");
            return string.Join(" | ", fields);
        }

        if (telemetry.TemperatureC is { } temperature)
            fields.Add($"temp {temperature}C");
        if (telemetry.FanPercent is { } fan)
            fields.Add($"fan {fan}%");
        if (telemetry.PowerWatts is { } power)
        {
            fields.Add($"power {power:F1}W");
            if (power > 0 && hashesPerSecond > 0)
                fields.Add($"eff {hashesPerSecond / 1_000_000 / power:F3} MH/s/W");
        }
        if (telemetry.GpuUtilizationPercent is { } gpuLoad)
            fields.Add($"load {gpuLoad}%");
        if (telemetry.MemoryUtilizationPercent is { } memoryLoad)
            fields.Add($"mem-load {memoryLoad}%");
        if (telemetry.MemoryUsedBytes is { } used && telemetry.MemoryTotalBytes is { } total)
            fields.Add($"VRAM {used / BytesPerGibibyte:F2}/{total / BytesPerGibibyte:F2} GiB");
        if (telemetry.GraphicsClockMhz is { } graphicsClock)
            fields.Add($"core {graphicsClock} MHz");
        if (telemetry.MemoryClockMhz is { } memoryClock)
            fields.Add($"mem {memoryClock} MHz");

        return string.Join(" | ", fields);
    }
}
