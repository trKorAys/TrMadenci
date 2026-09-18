namespace TrMadenci.NativeBridge;

public enum ComputeBackendKind
{
    Cuda,
    OpenCl
}

public sealed record ComputeDeviceId(
    ComputeBackendKind Backend,
    int PlatformIndex,
    int DeviceIndex)
{
    public override string ToString() => Backend == ComputeBackendKind.Cuda
        ? $"cuda:{DeviceIndex}"
        : $"opencl:{PlatformIndex}:{DeviceIndex}";

    public static ComputeDeviceId Parse(string value)
    {
        if (!TryParse(value, out var result))
            throw new FormatException($"Invalid compute device identifier '{value}'.");
        return result!;
    }

    public static bool TryParse(string? value, out ComputeDeviceId? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length == 2 &&
            string.Equals(parts[0], "cuda", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(parts[1], out var cudaDevice) && cudaDevice >= 0)
        {
            result = new ComputeDeviceId(ComputeBackendKind.Cuda, 0, cudaDevice);
            return true;
        }
        if (parts.Length == 3 &&
            string.Equals(parts[0], "opencl", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(parts[1], out var platform) && platform >= 0 &&
            int.TryParse(parts[2], out var openClDevice) && openClDevice >= 0)
        {
            result = new ComputeDeviceId(ComputeBackendKind.OpenCl, platform, openClDevice);
            return true;
        }
        return false;
    }
}

public sealed record ComputeDeviceInfo(
    ComputeDeviceId Id,
    string Name,
    string Vendor,
    string Runtime,
    ulong TotalMemoryBytes,
    uint ComputeUnits);

public interface IComputeBackend
{
    ComputeBackendKind Kind { get; }
    IReadOnlyList<ComputeDeviceInfo> GetDevices();
}

public sealed class CudaComputeBackend : IComputeBackend
{
    public ComputeBackendKind Kind => ComputeBackendKind.Cuda;

    public IReadOnlyList<ComputeDeviceInfo> GetDevices() => NativeDiagnostics.GetCudaDevices()
        .Select(device => new ComputeDeviceInfo(
            new ComputeDeviceId(Kind, 0, device.Index),
            device.Name,
            "NVIDIA",
            $"CUDA sm_{device.ComputeMajor}{device.ComputeMinor}",
            device.TotalMemoryBytes,
            0))
        .ToArray();
}

public sealed class OpenClComputeBackend : IComputeBackend
{
    public ComputeBackendKind Kind => ComputeBackendKind.OpenCl;

    public IReadOnlyList<ComputeDeviceInfo> GetDevices() => NativeDiagnostics.GetOpenClDevices()
        .Select(device => new ComputeDeviceInfo(
            new ComputeDeviceId(Kind, device.PlatformIndex, device.DeviceIndex),
            device.Name,
            device.Vendor,
            device.Version,
            device.TotalMemoryBytes,
            device.ComputeUnits))
        .ToArray();
}
