using TrMadenci.NativeBridge;

namespace TrMadenci.Core.Tests;

public sealed class ComputeBackendTests
{
    [Theory]
    [InlineData(ComputeBackendKind.Cuda, 0, 2, "cuda:2")]
    [InlineData(ComputeBackendKind.OpenCl, 1, 3, "opencl:1:3")]
    public void Device_ids_are_stable_and_backend_qualified(
        ComputeBackendKind backend,
        int platform,
        int device,
        string expected)
    {
        Assert.Equal(expected, new ComputeDeviceId(backend, platform, device).ToString());
    }

    [Fact]
    public void Backend_kinds_are_distinct_even_when_device_ordinals_match()
    {
        var cuda = new ComputeDeviceId(ComputeBackendKind.Cuda, 0, 0);
        var openCl = new ComputeDeviceId(ComputeBackendKind.OpenCl, 0, 0);

        Assert.NotEqual(cuda, openCl);
    }

    [Theory]
    [InlineData("cuda:2", ComputeBackendKind.Cuda, 0, 2)]
    [InlineData("CUDA:0", ComputeBackendKind.Cuda, 0, 0)]
    [InlineData("opencl:1:3", ComputeBackendKind.OpenCl, 1, 3)]
    [InlineData("OpenCL:0:0", ComputeBackendKind.OpenCl, 0, 0)]
    public void Stable_device_ids_round_trip(
        string value,
        ComputeBackendKind backend,
        int platform,
        int device)
    {
        var parsed = ComputeDeviceId.Parse(value);

        Assert.Equal(backend, parsed.Backend);
        Assert.Equal(platform, parsed.PlatformIndex);
        Assert.Equal(device, parsed.DeviceIndex);
        Assert.True(ComputeDeviceId.TryParse(parsed.ToString(), out var roundTrip));
        Assert.Equal(parsed, roundTrip);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cuda:-1")]
    [InlineData("opencl:0")]
    [InlineData("opencl:0:-1")]
    [InlineData("vulkan:0")]
    public void Invalid_device_ids_are_rejected(string value)
    {
        Assert.False(ComputeDeviceId.TryParse(value, out _));
        Assert.Throws<FormatException>(() => ComputeDeviceId.Parse(value));
    }
}
