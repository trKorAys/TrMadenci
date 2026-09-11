using System.Globalization;
using System.Text.Json;

namespace TrMadenci.Protocols.Stratum;

public sealed record OctopusJob(
    string JobId,
    ulong BlockHeight,
    byte[] HeaderHash,
    byte[] Boundary)
{
    public static OctopusJob Parse(StratumMessage message)
    {
        if (!string.Equals(message.Method, "mining.notify", StringComparison.Ordinal))
            throw new FormatException("Expected an Octopus mining.notify message.");
        if (!message.Root.TryGetProperty("params", out var parameters) ||
            parameters.ValueKind != JsonValueKind.Array || parameters.GetArrayLength() != 4)
            throw new FormatException("Octopus mining.notify must contain four parameters.");

        return new OctopusJob(
            RequiredString(parameters[0], "job id"),
            ParseBlockHeight(parameters[1]),
            Hex256(parameters[2], "header hash", requireFullWidth: true),
            Hex256(parameters[3], "boundary", requireFullWidth: false));
    }

    private static ulong ParseBlockHeight(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String &&
            ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number))
            return number;
        throw new FormatException("Octopus block height must be an unsigned decimal integer.");
    }

    private static string RequiredString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new FormatException($"Octopus {name} is missing.");

    private static byte[] Hex256(JsonElement value, string name, bool requireFullWidth)
    {
        var text = RequiredString(value, name);
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        if (text.Length == 0 || text.Length > 64 || (text.Length & 1) != 0 ||
            (requireFullWidth && text.Length != 64))
            throw new FormatException($"Octopus {name} must be an even-length hexadecimal value of at most 32 bytes.");

        try
        {
            return Convert.FromHexString(text.PadLeft(64, '0'));
        }
        catch (FormatException exception)
        {
            throw new FormatException($"Octopus {name} is not hexadecimal.", exception);
        }
    }
}
