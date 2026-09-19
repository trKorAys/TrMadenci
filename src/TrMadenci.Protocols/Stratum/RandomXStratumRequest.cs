using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrMadenci.Protocols.Stratum;

public sealed record RandomXStratumRequest(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] object Parameters)
{
    public string ToJsonLine() => JsonSerializer.Serialize(this) + "\n";

    public static RandomXStratumRequest Login(
        int id,
        string username,
        string password,
        string agent = "TrMadenci")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        return new(id, "2.0", "login", new
        {
            login = username,
            pass = password,
            agent
        });
    }

    public static RandomXStratumRequest Submit(
        int id,
        string sessionId,
        string jobId,
        uint nonce,
        byte[] result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Length != 32)
            throw new ArgumentException("RandomX share result must contain exactly 32 bytes.", nameof(result));

        Span<byte> nonceBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(nonceBytes, nonce);
        return new(id, "2.0", "submit", new
        {
            id = sessionId,
            job_id = jobId,
            nonce = Convert.ToHexString(nonceBytes).ToLowerInvariant(),
            result = Convert.ToHexString(result).ToLowerInvariant()
        });
    }
}
