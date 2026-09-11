using System.Globalization;
using System.Text.Json;

namespace TrMadenci.Protocols.Stratum;

public sealed record KawPowJob(
    string JobId,
    byte[] HeaderHash,
    byte[] SeedHash,
    byte[] Target,
    bool CleanJobs,
    ulong BlockHeight,
    uint BlockBits)
{
    public static KawPowJob Parse(StratumMessage message)
    {
        if (!string.Equals(message.Method, "mining.notify", StringComparison.Ordinal))
            throw new ArgumentException("Message is not a mining.notify notification.", nameof(message));

        var parameters = GetParameters(message.Root, expectedCount: 7);
        var jobId = RequiredString(parameters[0], "job id");
        var headerHash = DecodeHex(parameters[1], "header hash", 32);
        var seedHash = DecodeHex(parameters[2], "seed hash", 32);
        var target = DecodeHex(parameters[3], "target", 32);
        var cleanJobs = parameters[4].ValueKind is JsonValueKind.True or JsonValueKind.False
            ? parameters[4].GetBoolean()
            : throw new FormatException("clean_jobs must be a boolean.");
        var blockHeight = parameters[5].TryGetUInt64(out var height)
            ? height
            : throw new FormatException("Block height must be an unsigned integer.");
        var bitsText = RequiredString(parameters[6], "block bits");
        var blockBits = uint.TryParse(bitsText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits)
            ? bits
            : throw new FormatException("Block bits must be an 8-character hexadecimal value.");

        return new KawPowJob(jobId, headerHash, seedHash, target, cleanJobs, blockHeight, blockBits);
    }

    private static JsonElement GetParameters(JsonElement root, int expectedCount)
    {
        if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
            throw new FormatException("Stratum notification params must be an array.");
        if (parameters.GetArrayLength() != expectedCount)
            throw new FormatException($"Expected {expectedCount} job parameters, received {parameters.GetArrayLength()}.");
        return parameters;
    }

    private static string RequiredString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new FormatException($"The {name} must be a non-empty string.");

    private static byte[] DecodeHex(JsonElement value, string name, int byteCount)
    {
        var text = RequiredString(value, name);
        if (text.Length != byteCount * 2)
            throw new FormatException($"The {name} must contain exactly {byteCount} bytes.");

        try
        {
            return Convert.FromHexString(text);
        }
        catch (FormatException exception)
        {
            throw new FormatException($"The {name} contains invalid hexadecimal characters.", exception);
        }
    }
}
