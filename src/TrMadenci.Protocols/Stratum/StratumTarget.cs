namespace TrMadenci.Protocols.Stratum;

public sealed record StratumTarget(byte[] Bytes)
{
    public static StratumTarget Parse(StratumMessage message)
    {
        if (!string.Equals(message.Method, "mining.set_target", StringComparison.Ordinal))
            throw new ArgumentException("Message is not a mining.set_target notification.", nameof(message));

        if (!message.Root.TryGetProperty("params", out var parameters) ||
            parameters.ValueKind != System.Text.Json.JsonValueKind.Array ||
            parameters.GetArrayLength() != 1 ||
            parameters[0].ValueKind != System.Text.Json.JsonValueKind.String)
            throw new FormatException("mining.set_target must contain one hexadecimal target.");

        var text = parameters[0].GetString()!;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        if (text.Length != 64)
            throw new FormatException("Stratum target must contain exactly 32 bytes.");

        try
        {
            return new StratumTarget(Convert.FromHexString(text));
        }
        catch (FormatException exception)
        {
            throw new FormatException("Stratum target is not hexadecimal.", exception);
        }
    }
}
