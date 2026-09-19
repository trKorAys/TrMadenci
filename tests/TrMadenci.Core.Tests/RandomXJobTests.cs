using System.Text.Json;
using TrMadenci.Protocols.Stratum;

namespace TrMadenci.Core.Tests;

public sealed class RandomXJobTests
{
    private const string Blob =
        "0c0cbcc05f" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "00000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000000";
    private const string Seed =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Login_response_parses_session_and_initial_job()
    {
        using var message = StratumMessage.Parse($$$"""
            {"id":1,"jsonrpc":"2.0","error":null,"result":{
              "id":"session-7",
              "job":{"job_id":"job-1","blob":"{{{Blob}}}","target":"f0ffffff",
                     "seed_hash":"{{{Seed}}}","height":3456789,"algo":"rx/0"}
              }
            }
            """);

        var (sessionId, job) = RandomXJob.ParseLogin(message);

        Assert.Equal("session-7", sessionId);
        Assert.Equal("job-1", job.JobId);
        Assert.Equal(76, job.Blob.Length);
        Assert.Equal(32, job.SeedHash.Length);
        Assert.Equal(3_456_789UL, job.Height);
        Assert.Equal("rx/0", job.Algorithm);
        Assert.True(job.Target > uint.MaxValue);
    }

    [Fact]
    public void Job_notification_accepts_an_eight_byte_little_endian_target()
    {
        using var message = StratumMessage.Parse($$$"""
            {"jsonrpc":"2.0","method":"job","params":{
              "job_id":"job-2","blob":"{{{Blob}}}","target":"8877665544332211",
              "seed_hash":"{{{Seed}}}"}}
            """);

        var job = RandomXJob.ParseNotification(message);

        Assert.Equal(0x1122334455667788UL, job.Target);
        Assert.Null(job.Height);
    }

    [Fact]
    public void Short_blob_and_zero_target_are_rejected()
    {
        using var shortBlob = StratumMessage.Parse($$$"""
            {"method":"job","params":{"job_id":"j","blob":"00","target":"f0ffffff",
             "seed_hash":"{{{Seed}}}"}}
            """);
        using var zeroTarget = StratumMessage.Parse($$$"""
            {"method":"job","params":{"job_id":"j","blob":"{{{Blob}}}","target":"00000000",
             "seed_hash":"{{{Seed}}}"}}
            """);

        Assert.Throws<FormatException>(() => RandomXJob.ParseNotification(shortBlob));
        Assert.Throws<FormatException>(() => RandomXJob.ParseNotification(zeroTarget));
    }

    [Fact]
    public void Login_and_share_requests_use_monero_json_rpc_objects()
    {
        using var login = JsonDocument.Parse(
            RandomXStratumRequest.Login(1, "wallet.worker", "x").ToJsonLine());
        var loginParams = login.RootElement.GetProperty("params");
        Assert.Equal("login", login.RootElement.GetProperty("method").GetString());
        Assert.Equal("wallet.worker", loginParams.GetProperty("login").GetString());

        var result = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        using var submit = JsonDocument.Parse(
            RandomXStratumRequest.Submit(7, "session-7", "job-1", 0x12345678, result)
                .ToJsonLine());
        var submitParams = submit.RootElement.GetProperty("params");
        Assert.Equal("submit", submit.RootElement.GetProperty("method").GetString());
        Assert.Equal("78563412", submitParams.GetProperty("nonce").GetString());
        Assert.Equal(
            "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f",
            submitParams.GetProperty("result").GetString());
    }
}
