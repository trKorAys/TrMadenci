using TrMadenci.Protocols.Stratum;

namespace TrMadenci.Core.Tests;

public sealed class KawPowJobTests
{
    [Fact]
    public void Parses_binance_kawpow_job_shape()
    {
        const string json = """
            {"id":null,"method":"mining.notify","params":["1789083809_115","9cee2dd85e5e775b5d500d10a06cda573cac67b6fbbe2aa797801e90174ea29e","9a845f3f6ff2c8b20f8528e2af91156778be0b0a49030cbf2c58a86cc4462f17","00000003ffffc000000000000000000000000000000000000000000000000000",true,4532922,"1b06de98"]}
            """;
        using var message = StratumMessage.Parse(json);

        var job = KawPowJob.Parse(message);

        Assert.Equal("1789083809_115", job.JobId);
        Assert.Equal(32, job.HeaderHash.Length);
        Assert.Equal(32, job.SeedHash.Length);
        Assert.Equal(32, job.Target.Length);
        Assert.True(job.CleanJobs);
        Assert.Equal(4_532_922UL, job.BlockHeight);
        Assert.Equal(0x1b06de98U, job.BlockBits);
    }

    [Fact]
    public void Rejects_wrong_hash_length()
    {
        const string json = """
            {"id":null,"method":"mining.notify","params":["job","00","9a845f3f6ff2c8b20f8528e2af91156778be0b0a49030cbf2c58a86cc4462f17","00000003ffffc000000000000000000000000000000000000000000000000000",true,1,"1b06de98"]}
            """;
        using var message = StratumMessage.Parse(json);

        Assert.Throws<FormatException>(() => KawPowJob.Parse(message));
    }

    [Fact]
    public void Parses_kawpow_extranonce_as_high_nonce_prefix()
    {
        using var message = StratumMessage.Parse("""{"error":null,"id":1,"result":[null,"00a6"]}""");

        var subscription = KawPowSubscription.Parse(message);

        Assert.Equal("00a6", subscription.ExtraNonce);
        Assert.Equal(0x00a6000000000000UL, subscription.NonceBase);
        Assert.Equal(48, subscription.RemainingNonceBits);
    }

    [Fact]
    public void Formats_kawpow_share_in_stratum_order()
    {
        var request = StratumRequest.SubmitKawPow(7, "account.worker", "job", 0x1234,
            new byte[32], Enumerable.Repeat((byte)0xab, 32).ToArray());

        using var document = System.Text.Json.JsonDocument.Parse(request.ToJsonLine());
        var parameters = document.RootElement.GetProperty("params");
        Assert.Equal("account.worker", parameters[0].GetString());
        Assert.Equal("job", parameters[1].GetString());
        Assert.Equal("0x0000000000001234", parameters[2].GetString());
        Assert.Equal("0x" + new string('0', 64), parameters[3].GetString());
        Assert.Equal("0x" + string.Concat(Enumerable.Repeat("ab", 32)), parameters[4].GetString());
    }
}
