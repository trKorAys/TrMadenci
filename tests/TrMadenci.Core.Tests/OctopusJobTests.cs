using System.Text.Json;
using TrMadenci.Protocols.Stratum;

namespace TrMadenci.Core.Tests;

public sealed class OctopusJobTests
{
    [Fact]
    public void Binance_notify_parses_decimal_height_and_left_padded_boundary()
    {
        using var message = StratumMessage.Parse("""
            {"id":null,"method":"mining.notify","params":[
              "858","156498021",
              "0x0808fd83d234c6b06ba725672c6f3194852b4eba97a1164712c0c6b5932b5028",
              "0x40000000000000000000000000000000000000000000000000000000"]}
            """);

        var job = OctopusJob.Parse(message);

        Assert.Equal("858", job.JobId);
        Assert.Equal(156_498_021UL, job.BlockHeight);
        Assert.Equal(32, job.HeaderHash.Length);
        Assert.Equal(32, job.Boundary.Length);
        Assert.Equal(0x40, job.Boundary[4]);
        Assert.All(job.Boundary.Take(4), value => Assert.Equal(0, value));
    }

    [Fact]
    public void Subscription_carries_the_worker_identity_without_authorize()
    {
        var json = StratumRequest.SubscribeOctopus(1, "account.worker", "x").ToJsonLine();
        using var document = JsonDocument.Parse(json);
        var parameters = document.RootElement.GetProperty("params");

        Assert.Equal("mining.subscribe", document.RootElement.GetProperty("method").GetString());
        Assert.Equal("account.worker", parameters[0].GetString());
        Assert.Equal("x", parameters[1].GetString());
    }

    [Fact]
    public void Share_contains_worker_job_nonce_and_header()
    {
        var header = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var json = StratumRequest.SubmitOctopus(
            9, "account.worker", "858", 0x0123456789abcdef, header).ToJsonLine();
        using var document = JsonDocument.Parse(json);
        var parameters = document.RootElement.GetProperty("params");

        Assert.Equal("mining.submit", document.RootElement.GetProperty("method").GetString());
        Assert.Equal("account.worker", parameters[0].GetString());
        Assert.Equal("858", parameters[1].GetString());
        Assert.Equal("0x123456789abcdef", parameters[2].GetString());
        Assert.Equal("0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", parameters[3].GetString());
    }

    [Fact]
    public void Oversized_boundary_is_rejected()
    {
        using var message = StratumMessage.Parse(
            $"{{\"method\":\"mining.notify\",\"params\":[\"job\",\"1\",\"{new string('0', 64)}\",\"{new string('f', 66)}\"]}}");

        Assert.Throws<FormatException>(() => OctopusJob.Parse(message));
    }
}
