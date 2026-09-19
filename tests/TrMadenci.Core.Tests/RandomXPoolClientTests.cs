using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TrMadenci.Core.Configuration;
using TrMadenci.Core.Fees;
using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class RandomXPoolClientTests
{
    private const string Blob =
        "0c0cbcc05f" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "00000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000000";
    private const string Seed =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Login_initial_job_notification_and_share_submission_complete_over_tcp()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = RunServerAsync(listener, timeout.Token);
        var initialWork = new TaskCompletionSource<RandomXPoolWork>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationWork = new TaskCompletionSource<RandomXPoolWork>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var options = Pool(listener, "wallet.worker");

        try
        {
            await using var pool = new RandomXPoolClient(options, MiningBeneficiary.User, _ => { });
            pool.WorkReceived += work =>
            {
                if (work.Job.JobId == "initial-job")
                    initialWork.TrySetResult(work);
                if (work.Job.JobId == "next-job")
                    notificationWork.TrySetResult(work);
            };

            await pool.ConnectAsync(timeout.Token);
            var initial = await initialWork.Task.WaitAsync(timeout.Token);
            var next = await notificationWork.Task.WaitAsync(timeout.Token);
            Assert.Equal(MiningBeneficiary.User, initial.Beneficiary);
            Assert.Equal(3_456_789UL, next.Job.Height);
            Assert.Equal("rx/0", next.Job.Algorithm);

            var accepted = await pool.SubmitAsync(
                next.Job,
                0x12345678,
                Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(),
                timeout.Token);
            Assert.True(accepted);
            var submitted = await server.WaitAsync(timeout.Token);
            Assert.Equal("wallet.worker", pool.Username);
            Assert.Contains("\"nonce\":\"78563412\"", submitted, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Rejected_login_stops_without_substituting_a_destination()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var options = Pool(listener, "invalid.wallet");
        var server = RejectLoginAsync(listener, timeout.Token);

        try
        {
            await using var pool = new RandomXPoolClient(
                options,
                MiningBeneficiary.User,
                _ => { },
                reconnectDelay: TimeSpan.FromMilliseconds(10));
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                pool.ConnectAsync(timeout.Token));

            Assert.Contains("No configured user RandomX pool accepted", exception.Message);
            Assert.Equal("invalid.wallet", pool.Username);
            await server.WaitAsync(timeout.Token);
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

        var loginLine = await reader.ReadLineAsync(cancellationToken) ??
            throw new EndOfStreamException("Miner disconnected before RandomX login.");
        using (var login = JsonDocument.Parse(loginLine))
        {
            Assert.Equal("login", login.RootElement.GetProperty("method").GetString());
            Assert.Equal(
                "wallet.worker",
                login.RootElement.GetProperty("params").GetProperty("login").GetString());
        }

        await writer.WriteLineAsync(LoginResponse("initial-job").AsMemory(), cancellationToken);
        await writer.WriteLineAsync(Notification("next-job").AsMemory(), cancellationToken);
        var submitted = await reader.ReadLineAsync(cancellationToken) ??
            throw new EndOfStreamException("Miner disconnected before RandomX submission.");
        using var document = JsonDocument.Parse(submitted);
        var requestId = document.RootElement.GetProperty("id").GetInt32();
        await writer.WriteLineAsync(
            ($"{{\"id\":{requestId},\"jsonrpc\":\"2.0\",\"error\":null," +
            "\"result\":{\"status\":\"OK\"}}").AsMemory(),
            cancellationToken);
        return submitted;
    }

    private static async Task RejectLoginAsync(
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
        Assert.Contains("\"login\"", await reader.ReadLineAsync(cancellationToken));
        await writer.WriteLineAsync(
            ("{\"id\":1,\"jsonrpc\":\"2.0\",\"result\":null," +
            "\"error\":{\"code\":-1,\"message\":\"invalid login\"}}").AsMemory(),
            cancellationToken);
    }

    private static string LoginResponse(string jobId) =>
        "{\"id\":1,\"jsonrpc\":\"2.0\",\"error\":null,\"result\":{" +
        "\"id\":\"session-7\",\"job\":" + Job(jobId) + "}}";

    private static string Notification(string jobId) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"job\",\"params\":" + Job(jobId) + "}";

    private static string Job(string jobId) =>
        $"{{\"job_id\":\"{jobId}\",\"blob\":\"{Blob}\",\"target\":\"f0ffffff\"," +
        $"\"seed_hash\":\"{Seed}\",\"height\":3456789,\"algo\":\"rx/0\"}}";

    private static PoolOptions Pool(TcpListener listener, string username) => new()
    {
        Host = "127.0.0.1",
        Port = ((IPEndPoint)listener.LocalEndpoint).Port,
        Username = username,
        Password = "x"
    };
}
