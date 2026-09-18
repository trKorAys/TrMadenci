using TrMadenci.Core.Configuration;
using TrMadenci.NativeBridge;
using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class ComputeDeviceResolverTests
{
    [Fact]
    public void Auto_prefers_cuda_without_enumerating_opencl()
    {
        var cuda = Backend(ComputeBackendKind.Cuda, Device(ComputeBackendKind.Cuda, 0, 0));
        var openCl = Backend(
            ComputeBackendKind.OpenCl,
            Device(ComputeBackendKind.OpenCl, 0, 0),
            throwOnEnumeration: true);

        var selected = ComputeDeviceResolver.Resolve(Options(), cuda, openCl);

        Assert.Equal("cuda:0", Assert.Single(selected).Id.ToString());
        Assert.Equal(1, cuda.EnumerationCount);
        Assert.Equal(0, openCl.EnumerationCount);
    }

    [Fact]
    public void Auto_falls_back_to_opencl_when_cuda_is_absent()
    {
        var cuda = Backend(ComputeBackendKind.Cuda);
        var openCl = Backend(
            ComputeBackendKind.OpenCl,
            Device(ComputeBackendKind.OpenCl, 1, 2));

        var selected = ComputeDeviceResolver.Resolve(Options(), cuda, openCl);

        Assert.Equal("opencl:1:2", Assert.Single(selected).Id.ToString());
    }

    [Fact]
    public void Explicit_opencl_order_is_preserved()
    {
        var options = Options() with
        {
            ComputeBackend = ComputeBackendMode.OpenCl,
            ComputeDevices = ["opencl:1:0", "opencl:0:2"]
        };
        var openCl = Backend(
            ComputeBackendKind.OpenCl,
            Device(ComputeBackendKind.OpenCl, 0, 2),
            Device(ComputeBackendKind.OpenCl, 1, 0));

        var selected = ComputeDeviceResolver.Resolve(
            options, Backend(ComputeBackendKind.Cuda), openCl);

        Assert.Equal(["opencl:1:0", "opencl:0:2"], selected.Select(device => device.Id.ToString()));
    }

    [Fact]
    public void Missing_explicit_device_fails_before_work_starts()
    {
        var options = Options() with
        {
            ComputeBackend = ComputeBackendMode.Cuda,
            ComputeDevices = ["cuda:7"]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => ComputeDeviceResolver.Resolve(
            options,
            Backend(ComputeBackendKind.Cuda, Device(ComputeBackendKind.Cuda, 0, 0)),
            Backend(ComputeBackendKind.OpenCl)));

        Assert.Contains("cuda:7", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Auto_does_not_reinterpret_legacy_cuda_indexes_as_opencl_indexes()
    {
        var options = Options() with { GpuDevices = [0] };

        var exception = Assert.Throws<InvalidOperationException>(() => ComputeDeviceResolver.Resolve(
            options,
            Backend(ComputeBackendKind.Cuda),
            Backend(ComputeBackendKind.OpenCl, Device(ComputeBackendKind.OpenCl, 0, 0))));

        Assert.Contains("explicitly select CUDA", exception.Message, StringComparison.Ordinal);
    }

    private static MinerOptions Options() => new()
    {
        Coin = "rvn",
        Algorithm = "kawpow",
        Pool = new PoolOptions { Host = "pool", Port = 1, Username = "worker" }
    };

    private static FakeBackend Backend(
        ComputeBackendKind kind,
        ComputeDeviceInfo? device = null,
        bool throwOnEnumeration = false) =>
        new(kind, device is null ? [] : [device], throwOnEnumeration);

    private static FakeBackend Backend(
        ComputeBackendKind kind,
        ComputeDeviceInfo first,
        ComputeDeviceInfo second) =>
        new(kind, [first, second], false);

    private static ComputeDeviceInfo Device(
        ComputeBackendKind kind,
        int platform,
        int device) =>
        new(new ComputeDeviceId(kind, platform, device), $"GPU {device}", "vendor", "runtime", 8, 1);

    private sealed class FakeBackend(
        ComputeBackendKind kind,
        IReadOnlyList<ComputeDeviceInfo> devices,
        bool throwOnEnumeration) : IComputeBackend
    {
        public ComputeBackendKind Kind => kind;
        public int EnumerationCount { get; private set; }

        public IReadOnlyList<ComputeDeviceInfo> GetDevices()
        {
            EnumerationCount++;
            if (throwOnEnumeration)
                throw new InvalidOperationException("Backend must not be enumerated.");
            return devices;
        }
    }
}
