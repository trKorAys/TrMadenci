using System.Buffers.Binary;
using System.Numerics;

namespace TrMadenci.Core.Algorithms;

/// <summary>
/// Managed, allocation-bounded implementation of the Octopus multi-point
/// polynomial stage from Conflux Protocol Specification Appendix F.4.1.
/// This is a building block for the CPU oracle and CUDA cross-checks; it does
/// not generate the cache/DAG or constitute a complete Octopus hash.
/// </summary>
public static class OctopusMultiPoint
{
    public const uint Modulus = 1_032_193;
    public const uint Generator = 11;
    public const int WarpSize = 32;
    public const int Accesses = 32;
    public const int CoefficientCount = WarpSize * Accesses;
    public const ulong FnvPrime = 0x01000193;

    public static OctopusMultiPointResult Evaluate(ReadOnlySpan<byte> headerHash, ulong nonce)
    {
        if (headerHash.Length != 32)
            throw new ArgumentException("Octopus header hash must contain exactly 32 bytes.", nameof(headerHash));

        var v0 = BinaryPrimitives.ReadUInt64LittleEndian(headerHash);
        var v1 = BinaryPrimitives.ReadUInt64LittleEndian(headerHash[8..]);
        var v2 = BinaryPrimitives.ReadUInt64LittleEndian(headerHash[16..]);
        var v3 = BinaryPrimitives.ReadUInt64LittleEndian(headerHash[24..]);
        var a = Remap(v0);
        var b = Remap(v1);
        var c = ProperC(a, b, v2);
        var w = Remap(v3);
        var coefficients = GenerateCoefficients(v0, v1, v2, v3, nonce);

        var nonceLane = (uint)(nonce % WarpSize);
        var w2 = MultiplyMod(w, w);
        uint wPower = 1;
        uint w2Power = 1;
        for (var lane = 0U; lane < nonceLane; lane++)
        {
            wPower = MultiplyMod(wPower, w);
            w2Power = MultiplyMod(w2Power, w2);
        }

        var warpWPower = wPower;
        var warpW2Power = w2Power;
        for (var lane = nonceLane; lane < WarpSize; lane++)
        {
            warpWPower = MultiplyMod(warpWPower, w);
            warpW2Power = MultiplyMod(warpW2Power, w2);
        }

        var points = new uint[Accesses];
        ulong compressed = 0;
        for (var pointIndex = 0; pointIndex < Accesses; pointIndex++)
        {
            var x = (uint)(((ulong)a * w2Power + (ulong)b * wPower + c) % Modulus);
            uint polynomialValue = 0;
            for (var coefficient = CoefficientCount - 1; coefficient >= 0; coefficient--)
                polynomialValue = (uint)(((ulong)polynomialValue * x + coefficients[coefficient]) % Modulus);

            points[pointIndex] = polynomialValue;
            compressed = unchecked(compressed * FnvPrime) ^ polynomialValue;
            if (pointIndex + 1 < Accesses)
            {
                wPower = MultiplyMod(wPower, warpWPower);
                w2Power = MultiplyMod(w2Power, warpW2Power);
            }
        }

        return new OctopusMultiPointResult(a, b, c, w, compressed, points);
    }

    public static uint Remap(ulong value)
    {
        var exponent = value % (Modulus - 2UL) + 1;
        while (true)
        {
            var divisor = GreatestCommonDivisor(exponent, Modulus - 1UL);
            if (divisor == 1)
                break;
            exponent /= divisor;
        }
        return (uint)PowerMod(Generator, exponent);
    }

    private static uint ProperC(uint a, uint b, ulong initialValue)
    {
        var value = initialValue;
        while (true)
        {
            var candidate = Remap(value);
            if ((ulong)b * b % Modulus != 4UL * a * candidate % Modulus)
                return candidate;
            value = unchecked(value + 1);
        }
    }

    private static uint[] GenerateCoefficients(
        ulong v0,
        ulong v1,
        ulong v2,
        ulong v3,
        ulong nonce)
    {
        var coefficients = new uint[CoefficientCount];
        var warpStart = nonce / WarpSize * WarpSize;
        for (var lane = 0; lane < WarpSize; lane++)
        {
            var sip = new SipState(v0, v1, v2, v3);
            sip.Hash24(warpStart + (ulong)lane);
            for (var access = 0; access < Accesses; access++)
            {
                sip.Round();
                coefficients[access * WarpSize + lane] =
                    (uint)((sip.XorLanes & uint.MaxValue) % Modulus);
            }
        }
        return coefficients;
    }

    private static uint MultiplyMod(uint left, uint right) =>
        (uint)((ulong)left * right % Modulus);

    private static ulong PowerMod(uint value, ulong exponent)
    {
        ulong factor = value;
        ulong result = 1;
        while (exponent > 0)
        {
            if ((exponent & 1) != 0)
                result = result * factor % Modulus;
            factor = factor * factor % Modulus;
            exponent >>= 1;
        }
        return result;
    }

    private static ulong GreatestCommonDivisor(ulong left, ulong right)
    {
        while (right != 0)
            (left, right) = (right, left % right);
        return left;
    }

    private struct SipState(ulong v0, ulong v1, ulong v2, ulong v3)
    {
        private ulong _v0 = v0;
        private ulong _v1 = v1;
        private ulong _v2 = v2;
        private ulong _v3 = v3;

        public readonly ulong XorLanes => _v0 ^ _v1 ^ _v2 ^ _v3;

        public void Hash24(ulong value)
        {
            _v3 ^= value;
            Round();
            Round();
            _v0 ^= value;
            _v2 ^= 0xff;
            Round();
            Round();
            Round();
            Round();
        }

        public void Round()
        {
            _v0 = unchecked(_v0 + _v1);
            _v2 = unchecked(_v2 + _v3);
            _v1 = BitOperations.RotateLeft(_v1, 13);
            _v3 = BitOperations.RotateLeft(_v3, 16);
            _v1 ^= _v0;
            _v3 ^= _v2;
            _v0 = BitOperations.RotateLeft(_v0, 32);
            _v2 = unchecked(_v2 + _v1);
            _v0 = unchecked(_v0 + _v3);
            _v1 = BitOperations.RotateLeft(_v1, 17);
            _v3 = BitOperations.RotateLeft(_v3, 21);
            _v1 ^= _v2;
            _v3 ^= _v0;
            _v2 = BitOperations.RotateLeft(_v2, 32);
        }
    }
}

public sealed record OctopusMultiPointResult(
    uint A,
    uint B,
    uint C,
    uint W,
    ulong Compressed,
    IReadOnlyList<uint> Points);
