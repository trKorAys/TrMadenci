using System.Globalization;
using System.Text.Json;

namespace TrMadenci.Protocols.Stratum;

public sealed record KawPowSubscription(string ExtraNonce, ulong NonceBase, int RemainingNonceBits)
{
    public static KawPowSubscription Parse(StratumMessage message)
    {
        if (message.Id != 1 || message.HasError ||
            !message.Root.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Array || result.GetArrayLength() < 2 ||
            result[1].ValueKind != JsonValueKind.String)
            throw new FormatException("The KAWPOW subscribe response does not contain an extranonce.");

        var extraNonce = result[1].GetString() ?? "";
        if (extraNonce.Length is < 2 or > 8 || extraNonce.Length % 2 != 0 ||
            !uint.TryParse(extraNonce, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            throw new FormatException("KAWPOW extranonce must contain 1 to 4 hexadecimal bytes.");

        var padded = extraNonce.PadRight(16, '0');
        var nonceBase = ulong.Parse(padded, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new KawPowSubscription(extraNonce.ToLowerInvariant(), nonceBase, 64 - extraNonce.Length * 4);
    }
}
