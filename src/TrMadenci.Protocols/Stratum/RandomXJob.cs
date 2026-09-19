using System.Buffers.Binary;
using System.Text.Json;

namespace TrMadenci.Protocols.Stratum;

public sealed record RandomXJob(
    string JobId,
    byte[] Blob,
    byte[] SeedHash,
    ulong Target,
    ulong? Height,
    string? Algorithm)
{
    public const int NonceOffset = 39;
    public const int NonceSize = sizeof(uint);
    public const int MaximumBlobSize = 408;

    public static (string SessionId, RandomXJob Job) ParseLogin(StratumMessage message)
    {
        if (message.Id is null || message.HasError ||
            !message.Root.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Object)
            throw new FormatException("Expected a successful RandomX login response.");

        var sessionId = RequiredString(result, "id", "session id");
        if (!result.TryGetProperty("job", out var job) || job.ValueKind != JsonValueKind.Object)
            throw new FormatException("RandomX login response does not contain a job.");
        return (sessionId, ParseObject(job));
    }

    public static RandomXJob ParseNotification(StratumMessage message)
    {
        if (!string.Equals(message.Method, "job", StringComparison.Ordinal) ||
            !message.Root.TryGetProperty("params", out var parameters) ||
            parameters.ValueKind != JsonValueKind.Object)
            throw new FormatException("Expected a RandomX job notification.");
        return ParseObject(parameters);
    }

    private static RandomXJob ParseObject(JsonElement value)
    {
        var blob = Hex(RequiredString(value, "blob", "blob"), "blob");
        if (blob.Length < NonceOffset + NonceSize || blob.Length > MaximumBlobSize)
            throw new FormatException(
                $"RandomX blob must contain {NonceOffset + NonceSize}-{MaximumBlobSize} bytes.");

        var seed = Hex(RequiredString(value, "seed_hash", "seed hash"), "seed hash");
        if (seed.Length != 32)
            throw new FormatException("RandomX seed hash must contain exactly 32 bytes.");

        var targetText = RequiredString(value, "target", "target");
        var target = ParseTarget(targetText);
        ulong? height = null;
        if (value.TryGetProperty("height", out var heightValue))
        {
            if (heightValue.ValueKind != JsonValueKind.Number || !heightValue.TryGetUInt64(out var parsed))
                throw new FormatException("RandomX height must be an unsigned integer.");
            height = parsed;
        }

        string? algorithm = null;
        if (value.TryGetProperty("algo", out var algorithmValue))
        {
            if (algorithmValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(algorithmValue.GetString()))
                throw new FormatException("RandomX algorithm identifier is invalid.");
            algorithm = algorithmValue.GetString();
        }

        return new RandomXJob(
            RequiredString(value, "job_id", "job id"),
            blob,
            seed,
            target,
            height,
            algorithm);
    }

    private static ulong ParseTarget(string text)
    {
        var bytes = Hex(text, "target");
        if (bytes.Length == sizeof(uint))
        {
            var compact = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (compact == 0)
                throw new FormatException("RandomX compact target must be positive.");
            return ulong.MaxValue / (uint.MaxValue / (ulong)compact);
        }
        if (bytes.Length == sizeof(ulong))
        {
            var target = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            if (target == 0)
                throw new FormatException("RandomX target must be positive.");
            return target;
        }
        throw new FormatException("RandomX target must contain exactly 4 or 8 bytes.");
    }

    private static string RequiredString(JsonElement value, string propertyName, string displayName) =>
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new FormatException($"RandomX {displayName} is missing.");

    private static byte[] Hex(string value, string name)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            value = value[2..];
        if (value.Length == 0 || (value.Length & 1) != 0)
            throw new FormatException($"RandomX {name} must be even-length hexadecimal.");
        try
        {
            return Convert.FromHexString(value);
        }
        catch (FormatException exception)
        {
            throw new FormatException($"RandomX {name} is not hexadecimal.", exception);
        }
    }
}
