using System.Runtime.InteropServices;

namespace TrMadenci.NativeBridge;

public sealed record CudaDeviceInfo(
    int Index,
    string Name,
    ulong TotalMemoryBytes,
    int ComputeMajor,
    int ComputeMinor);

public sealed record GpuTelemetry(
    int DeviceIndex,
    uint? TemperatureC,
    uint? FanPercent,
    double? PowerWatts,
    uint? GpuUtilizationPercent,
    uint? MemoryUtilizationPercent,
    ulong? MemoryUsedBytes,
    ulong? MemoryTotalBytes,
    uint? GraphicsClockMhz,
    uint? MemoryClockMhz);

public sealed record KawPowEpochInfo(
    int EpochNumber,
    ulong LightCacheBytes,
    ulong FullDatasetBytes);

public sealed record EtcHashEpochInfo(
    int DatasetEpoch,
    int SeedEpoch,
    ulong LightCacheBytes,
    ulong FullDatasetBytes,
    byte[] SeedHash);

public sealed record CudaEpochBuildInfo(
    int EpochNumber,
    int DeviceIndex,
    ulong DatasetBytes,
    TimeSpan BuildTime);

public sealed record CudaSearchResult(
    bool SolutionFound,
    ulong Nonce,
    byte[] MixHash,
    byte[] FinalHash,
    ulong HashesSearched,
    TimeSpan SearchTime);

public sealed record KawPowHash(byte[] MixHash, byte[] FinalHash);

public sealed class CudaEpochContext : IDisposable
{
    private readonly NativeDiagnostics.CudaEpochHandle _handle;

    internal CudaEpochContext(NativeDiagnostics.CudaEpochHandle handle, CudaEpochBuildInfo buildInfo)
    {
        _handle = handle;
        BuildInfo = buildInfo;
    }

    public CudaEpochBuildInfo BuildInfo { get; }

    public CudaSearchResult Search(
        int blockNumber,
        byte[] headerHash,
        byte[] target,
        ulong startNonce,
        uint nonceCount) =>
        NativeDiagnostics.SearchCuda(_handle, blockNumber, headerHash, target, startNonce, nonceCount);

    public void Dispose() => _handle.Dispose();
}

public sealed class EtcHashCudaEpochContext : IDisposable
{
    private readonly NativeDiagnostics.CudaEpochHandle _handle;

    internal EtcHashCudaEpochContext(
        NativeDiagnostics.CudaEpochHandle handle,
        CudaEpochBuildInfo buildInfo)
    {
        _handle = handle;
        BuildInfo = buildInfo;
    }

    public CudaEpochBuildInfo BuildInfo { get; }

    public CudaSearchResult Search(
        int blockNumber,
        byte[] headerHash,
        byte[] target,
        ulong startNonce,
        uint nonceCount) =>
        NativeDiagnostics.SearchEtcHashCuda(
            _handle, blockNumber, headerHash, target, startNonce, nonceCount);

    public void Dispose() => _handle.Dispose();
}

public sealed class OctopusCudaEpochContext : IDisposable
{
    private readonly NativeDiagnostics.CudaEpochHandle _handle;

    internal OctopusCudaEpochContext(
        NativeDiagnostics.CudaEpochHandle handle,
        CudaEpochBuildInfo buildInfo)
    {
        _handle = handle;
        BuildInfo = buildInfo;
    }

    public CudaEpochBuildInfo BuildInfo { get; }

    public void Dispose() => _handle.Dispose();
}

public static class NativeDiagnostics
{
    private const uint TelemetryTemperature = 1u << 0;
    private const uint TelemetryFan = 1u << 1;
    private const uint TelemetryPower = 1u << 2;
    private const uint TelemetryUtilization = 1u << 3;
    private const uint TelemetryMemory = 1u << 4;
    private const uint TelemetryGraphicsClock = 1u << 5;
    private const uint TelemetryMemoryClock = 1u << 6;

    public static IReadOnlyList<CudaDeviceInfo> GetCudaDevices()
    {
        var count = NativeMethods.GetDeviceCount();
        if (count < 0)
            throw NativeFailure("CUDA device enumeration", count);

        var result = new List<CudaDeviceInfo>(count);
        for (var index = 0; index < count; index++)
        {
            var status = NativeMethods.GetDeviceInfo(index, out var info);
            if (status != 0)
                throw NativeFailure($"CUDA device {index} query", status);

            result.Add(new CudaDeviceInfo(
                info.Index,
                info.Name,
                info.TotalMemoryBytes,
                info.ComputeMajor,
                info.ComputeMinor));
        }
        return result;
    }

    public static bool TryGetGpuTelemetry(int deviceIndex, out GpuTelemetry? telemetry)
    {
        var status = NativeMethods.GetGpuTelemetry(deviceIndex, out var native);
        if (status != 0)
        {
            telemetry = null;
            return false;
        }

        var fields = native.ValidFields;
        telemetry = new GpuTelemetry(
            deviceIndex,
            Has(fields, TelemetryTemperature) ? native.TemperatureC : null,
            Has(fields, TelemetryFan) ? native.FanPercent : null,
            Has(fields, TelemetryPower) ? native.PowerMilliwatts / 1000d : null,
            Has(fields, TelemetryUtilization) ? native.GpuUtilizationPercent : null,
            Has(fields, TelemetryUtilization) ? native.MemoryUtilizationPercent : null,
            Has(fields, TelemetryMemory) ? native.MemoryUsedBytes : null,
            Has(fields, TelemetryMemory) ? native.MemoryTotalBytes : null,
            Has(fields, TelemetryGraphicsClock) ? native.GraphicsClockMhz : null,
            Has(fields, TelemetryMemoryClock) ? native.MemoryClockMhz : null);
        return true;
    }

    private static bool Has(uint fields, uint field) => (fields & field) != 0;

    public static KawPowEpochInfo GetEpochInfo(int blockNumber)
    {
        var status = NativeMethods.GetEpochInfo(blockNumber, out var info);
        if (status != 0)
            throw NativeFailure("KAWPOW epoch query", status);
        return new KawPowEpochInfo(info.EpochNumber, info.LightCacheBytes, info.FullDatasetBytes);
    }

    public static EtcHashEpochInfo GetEtcHashEpochInfo(int blockNumber)
    {
        var status = NativeMethods.GetEtcHashEpochInfo(blockNumber, out var info);
        if (status != 0)
            throw NativeFailure("ETCHash epoch query", status);
        return new EtcHashEpochInfo(
            info.DatasetEpoch,
            info.SeedEpoch,
            info.LightCacheBytes,
            info.FullDatasetBytes,
            info.SeedHash);
    }

    public static void ValidateEtcHashCudaDagItems(int blockNumber, uint itemCount)
    {
        if (itemCount == 0)
            throw new ArgumentOutOfRangeException(nameof(itemCount));
        var status = NativeMethods.ValidateEtcHashCudaDagItems(blockNumber, itemCount, out var mismatch);
        if (status == 1)
            throw new InvalidOperationException($"ETCHash CUDA DAG item {mismatch} differs from the CPU reference.");
        if (status != 0)
            throw NativeFailure("ETCHash CUDA DAG validation", status);
    }

    public static void ValidateOctopusCudaDagItems(ulong blockNumber, uint itemCount)
    {
        if (itemCount == 0)
            throw new ArgumentOutOfRangeException(nameof(itemCount));
        var status = NativeMethods.ValidateOctopusCudaDagItems(
            blockNumber, itemCount, out var mismatch);
        if (status != 0)
            throw new InvalidOperationException(
                $"Octopus CUDA DAG validation failed ({status}); first mismatch={mismatch}.");
    }

    public static int FindEtcHashSeedEpoch(byte[] seedHash)
    {
        if (seedHash.Length != 32)
            throw new ArgumentException("ETCHash seed must contain exactly 32 bytes.", nameof(seedHash));
        var status = NativeMethods.FindEtcHashSeedEpoch(seedHash, out var seedEpoch);
        if (status != 0)
            throw NativeFailure("ETCHash seed epoch lookup", status);
        return seedEpoch;
    }

    public static CudaEpochContext CreateCudaEpoch(int blockNumber, int deviceIndex)
    {
        var status = NativeMethods.CreateCudaEpoch(blockNumber, deviceIndex, out var pointer, out var info);
        if (status != 0)
            throw NativeFailure("CUDA DAG creation", status);

        var handle = new CudaEpochHandle(pointer);
        var buildInfo = new CudaEpochBuildInfo(
            info.EpochNumber,
            info.DeviceIndex,
            info.DatasetBytes,
            TimeSpan.FromMilliseconds(info.BuildMilliseconds));
        return new CudaEpochContext(handle, buildInfo);
    }

    public static EtcHashCudaEpochContext CreateEtcHashCudaEpoch(int blockNumber, int deviceIndex)
    {
        var status = NativeMethods.CreateEtcHashCudaEpoch(
            blockNumber, deviceIndex, out var pointer, out var info);
        if (status != 0)
            throw NativeFailure("ETCHash CUDA DAG creation", status);

        var handle = new CudaEpochHandle(pointer);
        var buildInfo = new CudaEpochBuildInfo(
            info.EpochNumber,
            info.DeviceIndex,
            info.DatasetBytes,
            TimeSpan.FromMilliseconds(info.BuildMilliseconds));
        return new EtcHashCudaEpochContext(handle, buildInfo);
    }

    public static OctopusCudaEpochContext CreateOctopusCudaEpoch(
        ulong blockNumber,
        int deviceIndex)
    {
        var status = NativeMethods.CreateOctopusCudaEpoch(
            blockNumber, deviceIndex, out var pointer, out var info);
        if (status != 0)
            throw NativeFailure("Octopus CUDA epoch creation", status);
        var handle = new CudaEpochHandle(pointer);
        var buildInfo = new CudaEpochBuildInfo(
            info.EpochNumber,
            info.DeviceIndex,
            info.DatasetBytes,
            TimeSpan.FromMilliseconds(info.BuildMilliseconds));
        return new OctopusCudaEpochContext(handle, buildInfo);
    }

    public static KawPowHash ComputeReferenceHash(int blockNumber, byte[] headerHash, ulong nonce)
    {
        if (headerHash.Length != 32)
            throw new ArgumentException("KAWPOW header must contain exactly 32 bytes.", nameof(headerHash));

        var mixHash = new byte[32];
        var finalHash = new byte[32];
        var status = NativeMethods.KawPowHashReference(
            blockNumber, headerHash, nonce, mixHash, finalHash);
        if (status != 0)
            throw NativeFailure("KAWPOW reference hash", status);
        return new KawPowHash(mixHash, finalHash);
    }

    public static KawPowHash ComputeEtcHashReferenceHash(int blockNumber, byte[] headerHash, ulong nonce)
    {
        if (headerHash.Length != 32)
            throw new ArgumentException("ETCHash header must contain exactly 32 bytes.", nameof(headerHash));

        var mixHash = new byte[32];
        var finalHash = new byte[32];
        var status = NativeMethods.EtcHashReference(
            blockNumber, headerHash, nonce, mixHash, finalHash);
        if (status != 0)
            throw NativeFailure("ETCHash reference hash", status);
        return new KawPowHash(mixHash, finalHash);
    }

    public static byte[] ComputeOctopusReferenceHash(
        ulong blockNumber,
        byte[] headerHash,
        ulong nonce,
        ulong compressedMultiPoint,
        uint[] points)
    {
        if (headerHash.Length != 32)
            throw new ArgumentException("Octopus header must contain exactly 32 bytes.", nameof(headerHash));
        if (points.Length != 32)
            throw new ArgumentException("Octopus multi-point result must contain exactly 32 points.", nameof(points));

        var finalHash = new byte[32];
        var status = NativeMethods.OctopusHashReference(
            blockNumber, headerHash, nonce, compressedMultiPoint, points, finalHash);
        if (status != 0)
            throw NativeFailure("Octopus reference hash", status);
        return finalHash;
    }

    internal static CudaSearchResult SearchCuda(
        CudaEpochHandle epoch,
        int blockNumber,
        byte[] headerHash,
        byte[] target,
        ulong startNonce,
        uint nonceCount)
    {
        if (headerHash.Length != 32 || target.Length != 32)
            throw new ArgumentException("KAWPOW header and target must contain exactly 32 bytes.");
        if (nonceCount == 0)
            throw new ArgumentOutOfRangeException(nameof(nonceCount));

        var status = NativeMethods.SearchCuda(
            epoch, blockNumber, headerHash, target, startNonce, nonceCount, out var result);
        if (status != 0)
            throw NativeFailure("CUDA nonce search", status);

        return new CudaSearchResult(
            result.SolutionFound != 0,
            result.Nonce,
            result.MixHash,
            result.FinalHash,
            result.HashesSearched,
            TimeSpan.FromMilliseconds(result.SearchMilliseconds));
    }

    internal static CudaSearchResult SearchEtcHashCuda(
        CudaEpochHandle epoch,
        int blockNumber,
        byte[] headerHash,
        byte[] target,
        ulong startNonce,
        uint nonceCount)
    {
        if (headerHash.Length != 32 || target.Length != 32)
            throw new ArgumentException("ETCHash header and target must contain exactly 32 bytes.");
        if (nonceCount == 0)
            throw new ArgumentOutOfRangeException(nameof(nonceCount));

        var status = NativeMethods.SearchEtcHashCuda(
            epoch, blockNumber, headerHash, target, startNonce, nonceCount, out var result);
        if (status != 0)
            throw NativeFailure("ETCHash CUDA nonce search", status);
        return new CudaSearchResult(
            result.SolutionFound != 0,
            result.Nonce,
            result.MixHash,
            result.FinalHash,
            result.HashesSearched,
            TimeSpan.FromMilliseconds(result.SearchMilliseconds));
    }

    private static InvalidOperationException NativeFailure(string operation, int status)
    {
        var pointer = NativeMethods.GetLastError();
        var detail = pointer == IntPtr.Zero ? "unknown native error" : Marshal.PtrToStringAnsi(pointer);
        return new InvalidOperationException($"{operation} failed ({status}): {detail}");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct NativeDeviceInfo
    {
        public int Index;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Name;

        public ulong TotalMemoryBytes;
        public int ComputeMajor;
        public int ComputeMinor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeEpochInfo
    {
        public int EpochNumber;
        public ulong LightCacheBytes;
        public ulong FullDatasetBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeEtcHashEpochInfo
    {
        public int DatasetEpoch;
        public int SeedEpoch;
        public ulong LightCacheBytes;
        public ulong FullDatasetBytes;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] SeedHash;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeGpuTelemetry
    {
        public uint ValidFields;
        public uint TemperatureC;
        public uint FanPercent;
        public uint PowerMilliwatts;
        public uint GpuUtilizationPercent;
        public uint MemoryUtilizationPercent;
        public ulong MemoryUsedBytes;
        public ulong MemoryTotalBytes;
        public uint GraphicsClockMhz;
        public uint MemoryClockMhz;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCudaEpochBuildInfo
    {
        public int EpochNumber;
        public int DeviceIndex;
        public ulong DatasetBytes;
        public double BuildMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSearchResult
    {
        public int SolutionFound;
        public ulong Nonce;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] MixHash;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] FinalHash;

        public ulong HashesSearched;
        public double SearchMilliseconds;
    }

    private static class NativeMethods
    {
        private const string Library = "TrMadenci.Native";

        [DllImport(Library, EntryPoint = "trmadenci_get_device_count", CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetDeviceCount();

        [DllImport(Library, EntryPoint = "trmadenci_get_device_info", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int GetDeviceInfo(int index, out NativeDeviceInfo info);

        [DllImport(Library, EntryPoint = "trmadenci_get_gpu_telemetry", CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetGpuTelemetry(int deviceIndex, out NativeGpuTelemetry telemetry);

        [DllImport(Library, EntryPoint = "trmadenci_get_epoch_info", CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetEpochInfo(int blockNumber, out NativeEpochInfo info);

        [DllImport(Library, EntryPoint = "trmadenci_etchash_get_epoch_info", CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetEtcHashEpochInfo(int blockNumber, out NativeEtcHashEpochInfo info);

        [DllImport(Library, EntryPoint = "trmadenci_validate_etchash_cuda_dag_items", CallingConvention = CallingConvention.Cdecl)]
        public static extern int ValidateEtcHashCudaDagItems(
            int blockNumber, uint itemCount, out uint firstMismatch);

        [DllImport(Library, EntryPoint = "trmadenci_validate_octopus_cuda_dag_items", CallingConvention = CallingConvention.Cdecl)]
        public static extern int ValidateOctopusCudaDagItems(
            ulong blockNumber, uint itemCount, out uint firstMismatch);

        [DllImport(Library, EntryPoint = "trmadenci_etchash_find_seed_epoch", CallingConvention = CallingConvention.Cdecl)]
        public static extern int FindEtcHashSeedEpoch(
            [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 32)] byte[] seedHash,
            out int seedEpoch);

        [DllImport(Library, EntryPoint = "trmadenci_kawpow_hash_reference", CallingConvention = CallingConvention.Cdecl)]
        public static extern int KawPowHashReference(
            int blockNumber,
            [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 32)] byte[] headerHash,
            ulong nonce,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 32)] byte[] mixHash,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 32)] byte[] finalHash);

        [DllImport(Library, EntryPoint = "trmadenci_etchash_hash_reference", CallingConvention = CallingConvention.Cdecl)]
        public static extern int EtcHashReference(
            int blockNumber,
            [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 32)] byte[] headerHash,
            ulong nonce,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 32)] byte[] mixHash,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 32)] byte[] finalHash);

        [DllImport(Library, EntryPoint = "trmadenci_octopus_hash_reference", CallingConvention = CallingConvention.Cdecl)]
        public static extern int OctopusHashReference(
            ulong blockNumber,
            [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 32)] byte[] headerHash,
            ulong nonce,
            ulong compressedMultiPoint,
            [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 32)] uint[] points,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 32)] byte[] finalHash);

        [DllImport(Library, EntryPoint = "trmadenci_create_cuda_epoch", CallingConvention = CallingConvention.Cdecl)]
        public static extern int CreateCudaEpoch(
            int blockNumber,
            int deviceIndex,
            out IntPtr epoch,
            out NativeCudaEpochBuildInfo buildInfo);

        [DllImport(Library, EntryPoint = "trmadenci_create_etchash_cuda_epoch", CallingConvention = CallingConvention.Cdecl)]
        public static extern int CreateEtcHashCudaEpoch(
            int blockNumber,
            int deviceIndex,
            out IntPtr epoch,
            out NativeCudaEpochBuildInfo buildInfo);

        [DllImport(Library, EntryPoint = "trmadenci_create_octopus_cuda_epoch", CallingConvention = CallingConvention.Cdecl)]
        public static extern int CreateOctopusCudaEpoch(
            ulong blockNumber,
            int deviceIndex,
            out IntPtr epoch,
            out NativeCudaEpochBuildInfo buildInfo);

        [DllImport(Library, EntryPoint = "trmadenci_destroy_cuda_epoch", CallingConvention = CallingConvention.Cdecl)]
        public static extern void DestroyCudaEpoch(IntPtr epoch);

        [DllImport(Library, EntryPoint = "trmadenci_search_cuda", CallingConvention = CallingConvention.Cdecl)]
        public static extern int SearchCuda(
            CudaEpochHandle epoch,
            int blockNumber,
            [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 32)] byte[] headerHash,
            [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 32)] byte[] target,
            ulong startNonce,
            uint nonceCount,
            out NativeSearchResult result);

        [DllImport(Library, EntryPoint = "trmadenci_search_etchash_cuda", CallingConvention = CallingConvention.Cdecl)]
        public static extern int SearchEtcHashCuda(
            CudaEpochHandle epoch,
            int blockNumber,
            [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 32)] byte[] headerHash,
            [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 32)] byte[] target,
            ulong startNonce,
            uint nonceCount,
            out NativeSearchResult result);

        [DllImport(Library, EntryPoint = "trmadenci_get_last_error", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr GetLastError();
    }

    internal sealed class CudaEpochHandle : SafeHandle
    {
        public CudaEpochHandle(IntPtr pointer) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(pointer);

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            NativeMethods.DestroyCudaEpoch(handle);
            return true;
        }
    }
}
