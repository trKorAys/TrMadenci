using System.Text.Json;
using TrMadenci.Protocols.Stratum;

namespace TrMadenci.Core.Tests;

public sealed class EtcHashJobTests
{
    [Fact]
    public void Binance_etc_notify_is_parsed_without_kawpow_extranonce_assumptions()
    {
        using var message = StratumMessage.Parse("""
            {"id":null,"method":"mining.notify","params":[
              "e142226fc512d1c6fcdcd35252d1b347d85075b695978776df002aae20b6c4f4",
              "e142226fc512d1c6fcdcd35252d1b347d85075b695978776df002aae20b6c4f4",
              "108aead9be9f05fe20e5bc5b839c49d608f0c12e64d406f257d0144a799cdfb2",
              "000000007fffffffffffffffffffffffffffffffffffffffffffffffffffffff",
              true]}
            """);

        var job = EtcHashJob.Parse(message);

        Assert.Equal(64, job.JobId.Length);
        Assert.Equal(32, job.HeaderHash.Length);
        Assert.Equal(32, job.SeedHash.Length);
        Assert.Equal(32, job.Target.Length);
        Assert.True(job.CleanJobs);
    }

    [Fact]
    public void Malformed_hash_is_rejected()
    {
        using var message = StratumMessage.Parse(
            "{\"method\":\"mining.notify\",\"params\":[\"job\",\"00\",\"00\",\"00\",true]}");

        Assert.Throws<FormatException>(() => EtcHashJob.Parse(message));
    }

    [Fact]
    public void Classic_stratum_share_contains_the_full_etchash_proof()
    {
        var header = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var mix = Enumerable.Range(32, 32).Select(value => (byte)value).ToArray();

        var json = StratumRequest.SubmitEtcHash(
            17, "account.worker", "job-42", 0x0123456789abcdef, header, mix).ToJsonLine();
        using var document = JsonDocument.Parse(json);
        var parameters = document.RootElement.GetProperty("params");

        Assert.Equal("mining.submit", document.RootElement.GetProperty("method").GetString());
        Assert.Equal("account.worker", parameters[0].GetString());
        Assert.Equal("job-42", parameters[1].GetString());
        Assert.Equal("0x0123456789abcdef", parameters[2].GetString());
        Assert.Equal("0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", parameters[3].GetString());
        Assert.Equal("0x202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f", parameters[4].GetString());
    }

    [Fact]
    public void Shared_target_notification_accepts_an_optional_hex_prefix()
    {
        using var message = StratumMessage.Parse(
            "{\"method\":\"mining.set_target\",\"params\":[\"0x000000007fffffffffffffffffffffffffffffffffffffffffffffffffffffff\"]}");

        var target = StratumTarget.Parse(message);

        Assert.Equal(32, target.Bytes.Length);
        Assert.Equal(0x7f, target.Bytes[4]);
    }
}
