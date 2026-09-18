using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TrMadenci.Core.Configuration;
using TrMadenci.Core.Fees;
using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class OctopusPoolClientTests
{
    [Fact]
    public async Task Authorization_rejection_stops_without_substituting_a_destination()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var options = Pool(listener, "invalid.worker");
        var server = RejectAuthorizationAsync(listener, timeout.Token);
        try
        {
            await using var pool = new OctopusPoolClient(
                options, MiningBeneficiary.User, _ => { }, reconnectDelay: TimeSpan.FromMilliseconds(10));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                pool.ConnectAsync(timeout.Token));
            Assert.Contains("No configured user pool accepted worker authorization", error.Message);
            Assert.Equal("invalid.worker", pool.Username);
            await server.WaitAsync(timeout.Token);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Disconnect_switches_to_failover_and_waits_for_fresh_work()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var primaryListener = new TcpListener(IPAddress.Loopback, 0);
        var failoverListener = new TcpListener(IPAddress.Loopback, 0);
        primaryListener.Start();
        failoverListener.Start();
        var primary = Pool(primaryListener, "primary.worker");
        var failover = Pool(failoverListener, "failover.worker");
        var primaryServer = ServeOneJobAsync(primaryListener, "primary-job", timeout.Token);
        var releaseFailover = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failoverServer = ServeOneJobAsync(
            failoverListener, "failover-job", timeout.Token, releaseFailover.Task);
        var failoverWork = new TaskCompletionSource<OctopusPoolWork>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await using var pool = new OctopusPoolClient(
                primary,
                MiningBeneficiary.User,
                _ => { },
                failover,
                TimeSpan.FromMilliseconds(10));
            pool.WorkReceived += work =>
            {
                if (work.Job.JobId == "failover-job")
                    failoverWork.TrySetResult(work);
            };

            await pool.ConnectAsync(timeout.Token);
            await primaryServer.WaitAsync(timeout.Token);
            var work = await failoverWork.Task.WaitAsync(timeout.Token);
            Assert.Equal("failover-job", work.Job.JobId);
            Assert.Equal($"127.0.0.1:{failover.Port}", pool.Endpoint);
            Assert.Equal("failover.worker", pool.Username);
            releaseFailover.TrySetResult();
            await failoverServer.WaitAsync(timeout.Token);
        }
        finally
        {
            releaseFailover.TrySetResult();
            primaryListener.Stop();
            failoverListener.Stop();
        }
    }

    [Fact]
    public async Task Tcp_flow_authenticates_in_subscription_parses_job_and_submits_nonce()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = RunServerAsync(listener, timeout.Token);
        var receivedWork = new TaskCompletionSource<OctopusPoolWork>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new PoolOptions
        {
            Host = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            Username = "account.worker",
            Password = "secret"
        };

        try
        {
            await using var pool = new OctopusPoolClient(
                options, MiningBeneficiary.User, _ => { });
            pool.WorkReceived += work => receivedWork.TrySetResult(work);

            await pool.ConnectAsync(timeout.Token);
            var work = await receivedWork.Task.WaitAsync(timeout.Token);
            Assert.Equal("cfx-job", work.Job.JobId);
            Assert.Equal(156_498_021UL, work.Job.BlockHeight);
            Assert.Equal(298, work.EpochNumber);
            Assert.Equal(MiningBeneficiary.User, work.Beneficiary);
            Assert.Equal(0x7f, work.Target[4]);

            var accepted = await pool.SubmitAsync(
                work.Job,
                new CudaShare(0x0123456789abcdef, new byte[32], new byte[32]),
                timeout.Token);
            Assert.True(accepted);

            var submittedLine = await server.WaitAsync(timeout.Token);
            using var submitted = JsonDocument.Parse(submittedLine);
            var root = submitted.RootElement;
            var parameters = root.GetProperty("params");
            Assert.Equal("mining.submit", root.GetProperty("method").GetString());
            Assert.Equal("account.worker", parameters[0].GetString());
            Assert.Equal("cfx-job", parameters[1].GetString());
            Assert.Equal("0x123456789abcdef", parameters[2].GetString());
            Assert.Equal($"0x{new string('1', 64)}", parameters[3].GetString());
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string> RunServerAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        var subscribeLine = await reader.ReadLineAsync(cancellationToken) ??
            throw new EndOfStreamException("Miner disconnected before subscribing.");
        using (var subscribe = JsonDocument.Parse(subscribeLine))
        {
            var root = subscribe.RootElement;
            var parameters = root.GetProperty("params");
            Assert.Equal("mining.subscribe", root.GetProperty("method").GetString());
            Assert.Equal("account.worker", parameters[0].GetString());
            Assert.Equal("secret", parameters[1].GetString());
        }

        await writer.WriteLineAsync(
            "{\"id\":1,\"result\":true,\"error\":null}".AsMemory(), cancellationToken);
        await writer.WriteLineAsync((
            "{\"id\":null,\"method\":\"mining.notify\",\"params\":[" +
            $"\"cfx-job\",\"156498021\",\"{new string('1', 64)}\"," +
            "\"000000007fffffffffffffffffffffffffffffffffffffffffffffffffffffff\"]}").AsMemory(),
            cancellationToken);

        var submitted = await reader.ReadLineAsync(cancellationToken) ??
            throw new EndOfStreamException("Miner disconnected before submitting the test share.");
        using var document = JsonDocument.Parse(submitted);
        var id = document.RootElement.GetProperty("id").GetInt32();
        await writer.WriteLineAsync(
            $"{{\"id\":{id},\"result\":true,\"error\":null}}".AsMemory(), cancellationToken);
        return submitted;
    }

    private static async Task RejectAuthorizationAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        Assert.Contains("mining.subscribe", await reader.ReadLineAsync(cancellationToken));
        await writer.WriteLineAsync(
            "{\"id\":1,\"result\":false,\"error\":[24,\"unauthorized worker\",null]}".AsMemory(),
            cancellationToken);
    }

    private static async Task ServeOneJobAsync(
        TcpListener listener,
        string jobId,
        CancellationToken cancellationToken,
        Task? holdConnection = null)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        Assert.Contains("mining.subscribe", await reader.ReadLineAsync(cancellationToken));
        await writer.WriteLineAsync(
            "{\"id\":1,\"result\":true,\"error\":null}".AsMemory(), cancellationToken);
        await writer.WriteLineAsync((
            "{\"id\":null,\"method\":\"mining.notify\",\"params\":[" +
            $"\"{jobId}\",\"156498021\",\"{new string('1', 64)}\"," +
            "\"000000007fffffffffffffffffffffffffffffffffffffffffffffffffffffff\"]}").AsMemory(),
            cancellationToken);
        if (holdConnection is not null)
            await holdConnection.WaitAsync(cancellationToken);
    }

    private static PoolOptions Pool(TcpListener listener, string username) => new()
    {
        Host = "127.0.0.1",
        Port = ((IPEndPoint)listener.LocalEndpoint).Port,
        Username = username,
        Password = "x"
    };
}
