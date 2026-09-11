using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrMadenci.Protocols.Stratum;

public sealed record StratumRequest(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] object[] Parameters)
{
    public string ToJsonLine() => JsonSerializer.Serialize(this) + "\n";

    public static StratumRequest Subscribe(int id, string agent) =>
        new(id, "mining.subscribe", [agent]);

    // Conflux pools authenticate as part of subscription; they do not use the
    // separate mining.authorize exchange used by KAWPOW and ETCHash pools.
    public static StratumRequest SubscribeOctopus(
        int id,
        string username,
        string password) =>
        new(id, "mining.subscribe", [username, password]);

    public static StratumRequest Authorize(int id, string username, string password) =>
        new(id, "mining.authorize", [username, password]);

    public static StratumRequest SubmitKawPow(
        int id,
        string username,
        string jobId,
        ulong nonce,
        byte[] headerHash,
        byte[] mixHash)
    {
        if (headerHash.Length != 32 || mixHash.Length != 32)
            throw new ArgumentException("KAWPOW header and mix hash must contain exactly 32 bytes.");

        return new(id, "mining.submit",
        [
            username,
            jobId,
            $"0x{nonce:x16}",
            $"0x{Convert.ToHexString(headerHash).ToLowerInvariant()}",
            $"0x{Convert.ToHexString(mixHash).ToLowerInvariant()}"
        ]);
    }

    public static StratumRequest SubmitEtcHash(
        int id,
        string username,
        string jobId,
        ulong nonce,
        byte[] headerHash,
        byte[] mixHash)
    {
        if (headerHash.Length != 32 || mixHash.Length != 32)
            throw new ArgumentException("ETCHash header and mix hash must contain exactly 32 bytes.");

        return new(id, "mining.submit",
        [
            username,
            jobId,
            $"0x{nonce:x16}",
            $"0x{Convert.ToHexString(headerHash).ToLowerInvariant()}",
            $"0x{Convert.ToHexString(mixHash).ToLowerInvariant()}"
        ]);
    }

    public static StratumRequest SubmitOctopus(
        int id,
        string username,
        string jobId,
        ulong nonce,
        byte[] headerHash)
    {
        if (headerHash.Length != 32)
            throw new ArgumentException("Octopus header hash must contain exactly 32 bytes.");

        return new(id, "mining.submit",
        [
            username,
            jobId,
            $"0x{nonce:x}",
            $"0x{Convert.ToHexString(headerHash).ToLowerInvariant()}"
        ]);
    }
}
