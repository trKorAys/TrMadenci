using TrMadenci.Core.Algorithms;
using TrMadenci.NativeBridge;

namespace TrMadenci.Core.Tests;

public sealed class OctopusMultiPointTests
{
    private static readonly byte[] Header = Convert.FromHexString(
        "4D99D0B41C7EB0DD1A801C35AAE2DF28AE6B53BC7743F0818A34B6EC97F5B4AE");

    [Fact]
    public void Result_has_the_protocol_defined_shape_and_field_range()
    {
        var result = OctopusMultiPoint.Evaluate(Header, 0x2333333320);

        Assert.Equal(OctopusMultiPoint.Accesses, result.Points.Count);
        Assert.All(result.Points, point => Assert.True(point < OctopusMultiPoint.Modulus));
        Assert.InRange(result.A, 1U, OctopusMultiPoint.Modulus - 1);
        Assert.InRange(result.B, 1U, OctopusMultiPoint.Modulus - 1);
        Assert.InRange(result.C, 1U, OctopusMultiPoint.Modulus - 1);
        Assert.InRange(result.W, 1U, OctopusMultiPoint.Modulus - 1);
        Assert.NotEqual(
            (ulong)result.B * result.B % OctopusMultiPoint.Modulus,
            4UL * result.A * result.C % OctopusMultiPoint.Modulus);
        Assert.Equal(0xe4592b66ac531f54UL, result.Compressed);
        Assert.Equal(
        [
            0x54702U, 0x7e047U, 0xddb90U, 0x2c95dU, 0xf2fadU, 0xd4928U, 0x532ebU, 0x78923U,
            0x5789dU, 0x6f000U, 0x9081aU, 0x54caeU, 0x4dadfU, 0x12006U, 0x01345U, 0x429c6U,
            0x00d93U, 0x56640U, 0xeac64U, 0x2bf07U, 0xbcaf7U, 0x68da6U, 0x77eccU, 0x560b4U,
            0x712d0U, 0x68432U, 0xf09fcU, 0x4f23aU, 0xf9b9eU, 0x7794aU, 0xe76ddU, 0x95ddeU
        ], result.Points);
    }

    [Fact]
    public void Nonces_in_one_warp_share_coefficients_but_select_different_points()
    {
        var first = OctopusMultiPoint.Evaluate(Header, 0x2333333320);
        var nextLane = OctopusMultiPoint.Evaluate(Header, 0x2333333321);

        Assert.Equal((first.A, first.B, first.C, first.W),
            (nextLane.A, nextLane.B, nextLane.C, nextLane.W));
        Assert.NotEqual(first.Compressed, nextLane.Compressed);
        Assert.NotEqual(first.Points, nextLane.Points);
    }

    [Fact]
    public void Same_input_is_deterministic()
    {
        var first = OctopusMultiPoint.Evaluate(Header, 12345);
        var second = OctopusMultiPoint.Evaluate(Header, 12345);

        Assert.Equal(first.Compressed, second.Compressed);
        Assert.Equal(first.Points, second.Points);
    }

    [Fact]
    public void Header_must_be_256_bits()
    {
        Assert.Throws<ArgumentException>(() => OctopusMultiPoint.Evaluate(new byte[31], 0));
    }

    [Fact]
    public void Full_epoch_zero_hash_matches_the_independent_reference()
    {
        const ulong nonce = 0x2333333320;
        var multiPoint = OctopusMultiPoint.Evaluate(Header, nonce);

        var hash = NativeDiagnostics.ComputeOctopusReferenceHash(
            2, Header, nonce, multiPoint.Compressed, multiPoint.Points.ToArray());

        Assert.Equal(
            "D45C965D3707E27A42995132637854234385CBF5626897259F1EE980554DDD5C",
            Convert.ToHexString(hash));
    }
}
