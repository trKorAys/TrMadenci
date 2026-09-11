using System.Text.Json;

namespace TrMadenci.Protocols.Stratum;

public sealed record EtcHashJob(
    string JobId,
    byte[] HeaderHash,
    byte[] SeedHash,
    byte[] Target,
    bool CleanJobs)
{
    public static EtcHashJob Parse(StratumMessage message)
    {
        if (!string.Equals(message.Method, "mining.notify", StringComparison.Ordinal))
            throw new FormatException("Expected an ETCHash mining.notify message.");
        if (!message.Root.TryGetProperty("params", out var parameters) ||
            parameters.ValueKind != JsonValueKind.Array || parameters.GetArrayLength() < 5)
            throw new FormatException("ETCHash mining.notify must contain five parameters.");

        return new EtcHashJob(
            RequiredString(parameters[0], "job id"),
            Hash(parameters[1], "header hash"),
            Hash(parameters[2], "seed hash"),
            Hash(parameters[3], "target"),
            parameters[4].ValueKind is JsonValueKind.True or JsonValueKind.False
                ? parameters[4].GetBoolean()
                : throw new FormatException("ETCHash clean-jobs flag must be boolean."));
    }

    private static string RequiredString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new FormatException($"ETCHash {name} is missing.");

    private static byte[] Hash(JsonElement value, string name)
    {
        var text = RequiredString(value, name);
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        if (text.Length != 64)
            throw new FormatException($"ETCHash {name} must contain exactly 32 bytes.");
        try
        {
            return Convert.FromHexString(text);
        }
        catch (FormatException exception)
        {
            throw new FormatException($"ETCHash {name} is not hexadecimal.", exception);
        }
    }
}
