using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrMadenci.Core.Configuration;

public enum ComputeBackendMode
{
    Auto,
    Cuda,
    OpenCl,
    Cpu
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MinerOptions
{
    public string Coin { get; init; } = "rvn";
    public string Algorithm { get; init; } = "kawpow";
    public PoolOptions Pool { get; init; } = new();
    public PoolOptions? FailoverPool { get; init; }
    [JsonConverter(typeof(ComputeBackendModeConverter))]
    public ComputeBackendMode ComputeBackend { get; init; } = ComputeBackendMode.Auto;
    public int[] GpuDevices { get; init; } = [];
    public string[] ComputeDevices { get; init; } = [];
    public int CpuThreads { get; init; }
    public bool CpuHugePages { get; init; } = true;
    public bool CpuSecureJit { get; init; } = true;

    public void Validate()
    {
        var profile = CoinProfileCatalog.GetRequired(Coin);
        if (!string.Equals(Algorithm, profile.Algorithm, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Coin '{profile.Ticker}' requires algorithm '{profile.Algorithm}'.");

        Pool.Validate(nameof(Pool));
        FailoverPool?.Validate(nameof(FailoverPool));
        var isRandomX = string.Equals(profile.Algorithm, "randomx", StringComparison.OrdinalIgnoreCase);
        if (isRandomX && ComputeBackend != ComputeBackendMode.Cpu)
            throw new ArgumentException("RandomX profiles require computeBackend 'cpu'.");
        if (!isRandomX && ComputeBackend == ComputeBackendMode.Cpu)
            throw new ArgumentException("computeBackend 'cpu' is currently reserved for RandomX profiles.");
        if (CpuThreads is < 0 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(CpuThreads), "cpuThreads must be between 0 and 1024.");
        if (GpuDevices.Distinct().Count() != GpuDevices.Length || GpuDevices.Any(index => index < 0))
            throw new ArgumentException("GPU device indexes must be unique and non-negative.");
        if (ComputeDevices.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ComputeDevices.Length)
            throw new ArgumentException("Compute device identifiers must be unique.");
        if (ComputeDevices.Length > 0 && GpuDevices.Length > 0)
            throw new ArgumentException("Use either computeDevices or the legacy gpuDevices list, not both.");
        if (ComputeBackend == ComputeBackendMode.Auto && ComputeDevices.Length > 0)
            throw new ArgumentException("Explicit computeDevices require computeBackend 'cuda' or 'openCl'.");
        if (ComputeBackend == ComputeBackendMode.OpenCl && GpuDevices.Length > 0)
            throw new ArgumentException("OpenCL selection uses computeDevices identifiers such as 'opencl:0:0'.");
        if (ComputeBackend == ComputeBackendMode.Cpu &&
            (ComputeDevices.Length > 0 || GpuDevices.Length > 0))
            throw new ArgumentException("CPU mining does not accept GPU or compute-device selections.");
        foreach (var device in ComputeDevices)
        {
            if (!TryParseComputeDevice(device, out var backend, out _, out _))
                throw new ArgumentException($"Invalid compute device identifier '{device}'.");
            if ((ComputeBackend == ComputeBackendMode.Cuda && backend != ComputeBackendMode.Cuda) ||
                (ComputeBackend == ComputeBackendMode.OpenCl && backend != ComputeBackendMode.OpenCl))
                throw new ArgumentException(
                    $"Compute device '{device}' does not match backend '{ComputeBackend}'.");
        }
    }

    public int[] GetCudaDeviceIndexes() => ComputeBackend is ComputeBackendMode.OpenCl or ComputeBackendMode.Cpu
        ? []
        : ComputeDevices.Length == 0
            ? GpuDevices
            : ComputeDevices.Select(device => int.Parse(device.AsSpan("cuda:".Length))).ToArray();

    private static bool TryParseComputeDevice(
        string value,
        out ComputeBackendMode backend,
        out int platformIndex,
        out int deviceIndex)
    {
        backend = ComputeBackendMode.Auto;
        platformIndex = 0;
        deviceIndex = -1;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && string.Equals(parts[0], "cuda", StringComparison.OrdinalIgnoreCase))
        {
            backend = ComputeBackendMode.Cuda;
            return int.TryParse(parts[1], out deviceIndex) && deviceIndex >= 0;
        }
        if (parts.Length == 3 && string.Equals(parts[0], "opencl", StringComparison.OrdinalIgnoreCase))
        {
            backend = ComputeBackendMode.OpenCl;
            return int.TryParse(parts[1], out platformIndex) && platformIndex >= 0 &&
                int.TryParse(parts[2], out deviceIndex) && deviceIndex >= 0;
        }
        return false;
    }
}

public sealed class ComputeBackendModeConverter : JsonConverter<ComputeBackendMode>
{
    public override ComputeBackendMode Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String ||
            !Enum.TryParse<ComputeBackendMode>(reader.GetString(), true, out var value) ||
            !Enum.IsDefined(value))
            throw new JsonException("computeBackend must be one of: auto, cuda, openCl, cpu.");
        return value;
    }

    public override void Write(
        Utf8JsonWriter writer,
        ComputeBackendMode value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            ComputeBackendMode.Auto => "auto",
            ComputeBackendMode.Cuda => "cuda",
            ComputeBackendMode.OpenCl => "openCl",
            ComputeBackendMode.Cpu => "cpu",
            _ => throw new JsonException($"Unknown compute backend '{value}'.")
        });
}

public sealed record PoolOptions
{
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Username { get; init; } = "";
    public string Password { get; init; } = "x";

    public void Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(Host))
            throw new ArgumentException($"{name} host is required.");
        if (Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException($"{name}.Port");
        if (string.IsNullOrWhiteSpace(Username))
            throw new ArgumentException($"{name} username/worker is required.");
    }
}
