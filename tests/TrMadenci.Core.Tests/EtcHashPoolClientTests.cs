using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TrMadenci.Core.Configuration;
using TrMadenci.Core.Fees;
using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class EtcHashPoolClientTests
{
    [Fact]
    public async Task Initial_authorization_rejection_stops_without_substituting_a_destination()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var options = Pool("127.0.0.1", listener, "invalid.worker");
        var server = RejectAuthorizationAsync(listener, timeout.Token);

        try
        {
            await using var pool = new EtcHashPoolClient(
                options,
                MiningBeneficiary.User,
                _ => { },
                resolveSeedEpoch: _ => 0);

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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var primaryListener = new TcpListener(IPAddress.Loopback, 0);
        var failoverListener = new TcpListener(IPAddress.Loopback, 0);
        primaryListener.Start();
        failoverListener.Start();
        var primary = Pool("127.0.0.1", primaryListener, "primary.worker");
        var failover = Pool("127.0.0.1", failoverListener, "failover.worker");
        var releaseFailover = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primaryServer = ServeJobAsync(primaryListener, "primary-job", null, timeout.Token);
        var failoverServer = ServeJobAsync(
            failoverListener, "failover-job", releaseFailover.Task, timeout.Token);
        var failoverWork = new TaskCompletionSource<EtcHashPoolWork>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await using var pool = new EtcHashPoolClient(
                primary,
                MiningBeneficiary.User,
                _ => { },
                failover,
                _ => 844);
            pool.WorkReceived += work =>
            {
                if (work.Job.JobId == "failover-job")
                    failoverWork.TrySetResult(work);
            };
            pool.ConnectionLost += _ => disconnected.TrySetResult();

            await pool.ConnectAsync(timeout.Token);
            await primaryServer.WaitAsync(timeout.Token);
            await disconnected.Task.WaitAsync(timeout.Token);
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
    public async Task Live_tcp_flow_resolves_epoch_and_submits_the_complete_share()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunServerAsync(listener, timeout.Token);
        var receivedWork = new TaskCompletionSource<EtcHashPoolWork>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new PoolOptions
        {
            Host = "127.0.0.1",
            Port = port,
            Username = "account.worker",
            Password = "x"
        };

        try
        {
            await using var pool = new EtcHashPoolClient(
                options,
                MiningBeneficiary.User,
                _ => { },
                resolveSeedEpoch: seed =>
                {
                    Assert.Equal(32, seed.Length);
                    return 844;
                });
            pool.WorkReceived += work => receivedWork.TrySetResult(work);

            await pool.ConnectAsync(timeout.Token);
            var work = await receivedWork.Task.WaitAsync(timeout.Token);

            Assert.Equal(844, work.SeedEpoch);
            Assert.Equal(422, work.DatasetEpoch);
            Assert.Equal(25_320_000, work.RepresentativeBlock);
            Assert.Equal(MiningBeneficiary.User, work.Beneficiary);
            Assert.Equal(0x7f, work.Target[4]);
            Assert.All(work.Target.Take(4), value => Assert.Equal(0, value));

            var accepted = await pool.SubmitAsync(
                work.Job,
                new CudaShare(
                    0x0123456789abcdef,
                    Enumerable.Repeat((byte)0x22, 32).ToArray(),
                    new byte[32]),
                timeout.Token);
            Assert.True(accepted);

            var submittedLine = await server.WaitAsync(timeout.Token);
            using var submitted = JsonDocument.Parse(submittedLine);
            var root = submitted.RootElement;
            var parameters = root.GetProperty("params");
            Assert.Equal("mining.submit", root.GetProperty("method").GetString());
            Assert.Equal("account.worker", parameters[0].GetString());
            Assert.Equal("job-1", parameters[1].GetString());
            Assert.Equal("0x0123456789abcdef", parameters[2].GetString());
            Assert.Equal($"0x{new string('1', 64)}", parameters[3].GetString());
            Assert.Equal($"0x{new string('2', 64)}", parameters[4].GetString());
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string> RunServerAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        var subscribe = await reader.ReadLineAsync(cancellationToken);
        var authorize = await reader.ReadLineAsync(cancellationToken);
        Assert.Contains("mining.subscribe", subscribe);
        Assert.Contains("mining.authorize", authorize);
        await writer.WriteLineAsync("{\"id\":1,\"result\":true,\"error\":null}".AsMemory(), cancellationToken);
        await writer.WriteLineAsync("{\"id\":2,\"result\":true,\"error\":null}".AsMemory(), cancellationToken);
        await writer.WriteLineAsync(
            "{\"id\":null,\"method\":\"mining.set_target\",\"params\":[\"000000007fffffffffffffffffffffffffffffffffffffffffffffffffffffff\"]}".AsMemory(),
            cancellationToken);
        await writer.WriteLineAsync((
            "{\"id\":null,\"method\":\"mining.notify\",\"params\":[" +
            $"\"job-1\",\"{new string('1', 64)}\",\"{new string('0', 64)}\"," +
            $"\"{new string('f', 64)}\",true]}}").AsMemory(), cancellationToken);

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
        Assert.Contains("mining.authorize", await reader.ReadLineAsync(cancellationToken));
        await writer.WriteLineAsync(
            "{\"id\":1,\"result\":true,\"error\":null}".AsMemory(), cancellationToken);
        await writer.WriteLineAsync(
            "{\"id\":2,\"result\":false,\"error\":[24,\"unauthorized worker\",null]}".AsMemory(),
            cancellationToken);
    }

    private static PoolOptions Pool(string host, TcpListener listener, string username) => new()
    {
        Host = host,
        Port = ((IPEndPoint)listener.LocalEndpoint).Port,
        Username = username,
        Password = "x"
    };

    private static async Task ServeJobAsync(
        TcpListener listener,
        string jobId,
        Task? holdConnection,
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
        Assert.Contains("mining.authorize", await reader.ReadLineAsync(cancellationToken));
        await writer.WriteLineAsync("{\"id\":1,\"result\":true,\"error\":null}".AsMemory(), cancellationToken);
        await writer.WriteLineAsync("{\"id\":2,\"result\":true,\"error\":null}".AsMemory(), cancellationToken);
        await writer.WriteLineAsync((
            "{\"id\":null,\"method\":\"mining.notify\",\"params\":[" +
            $"\"{jobId}\",\"{new string('1', 64)}\",\"{new string('0', 64)}\"," +
            $"\"{new string('f', 64)}\",true]}}").AsMemory(), cancellationToken);
        if (holdConnection is not null)
            await holdConnection.WaitAsync(cancellationToken);
    }
}
