using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using TrMadenci.Core.Configuration;
using TrMadenci.Core.Fees;
using TrMadenci.Protocols.Stratum;

namespace TrMadenci.Service.Mining;

internal sealed record RandomXPoolWork(
    RandomXPoolClient Pool,
    RandomXJob Job,
    NonceAllocator Nonces,
    MiningBeneficiary Beneficiary);

internal sealed class RandomXPoolClient(
    PoolOptions options,
    MiningBeneficiary beneficiary,
    Action<string> log,
    PoolOptions? failover = null,
    TimeSpan? reconnectDelay = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _submissions = new();
    private readonly TaskCompletionSource _firstConnection = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _connectionGate = new();
    private readonly PoolOptions[] _endpoints = failover is null ? [options] : [options, failover];
    private StratumConnection? _connection;
    private PoolOptions? _activeOptions;
    private string? _sessionId;
    private Task? _connectionTask;
    private int _nextRequestId = 100;

    public event Action<RandomXPoolWork>? WorkReceived;
    public event Action<RandomXPoolClient>? ConnectionLost;

    public string Endpoint => $"{(_activeOptions ?? options).Host}:{(_activeOptions ?? options).Port}";
    public string Username => (_activeOptions ?? options).Username;
    public MiningBeneficiary Beneficiary => beneficiary;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _connectionTask ??= Task.Run(() => ConnectionLoopAsync(_lifetime.Token), CancellationToken.None);
        await _firstConnection.Task.WaitAsync(cancellationToken);
    }

    public async Task<bool> SubmitAsync(
        RandomXJob job,
        uint nonce,
        byte[] result,
        CancellationToken cancellationToken)
    {
        StratumConnection connection;
        string sessionId;
        lock (_connectionGate)
        {
            connection = _connection ??
                throw new InvalidOperationException($"{beneficiary} RandomX pool is reconnecting.");
            sessionId = _sessionId ??
                throw new InvalidOperationException($"{beneficiary} RandomX pool session is unavailable.");
        }

        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_submissions.TryAdd(id, completion))
            throw new InvalidOperationException("Could not allocate a RandomX submission id.");
        try
        {
            await connection.SendAsync(
                RandomXStratumRequest.Submit(id, sessionId, job.JobId, nonce, result),
                cancellationToken);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        finally
        {
            _submissions.TryRemove(id, out _);
        }
    }

    private async Task ConnectionLoopAsync(CancellationToken cancellationToken)
    {
        var endpointIndex = 0;
        var delay = reconnectDelay ?? TimeSpan.FromSeconds(5);
        while (!cancellationToken.IsCancellationRequested)
        {
            var currentOptions = _endpoints[endpointIndex % _endpoints.Length];
            await using var connection = new StratumConnection();
            try
            {
                await connection.ConnectAsync(currentOptions.Host, currentOptions.Port, cancellationToken);
                lock (_connectionGate)
                {
                    _connection = connection;
                    _activeOptions = currentOptions;
                    _sessionId = null;
                }
                await connection.SendAsync(
                    RandomXStratumRequest.Login(
                        1, currentOptions.Username, currentOptions.Password, "TrMadenci/0.1.0"),
                    cancellationToken);

                var authenticated = false;
                while (!cancellationToken.IsCancellationRequested)
                {
                    using var message = await connection.ReadAsync(cancellationToken);
                    if (message.Id == 1)
                    {
                        if (message.HasError)
                            throw new PoolAuthorizationException(
                                $"RandomX pool login rejected for {currentOptions.Username}.");
                        var (sessionId, job) = RandomXJob.ParseLogin(message);
                        lock (_connectionGate)
                            _sessionId = sessionId;
                        authenticated = true;
                        log($"[{beneficiary}] RandomX pool ready: {Endpoint} / {Username}");
                        _firstConnection.TrySetResult();
                        Publish(job);
                        continue;
                    }
                    if (message.Id is { } responseId &&
                        _submissions.TryRemove(responseId, out var submission))
                    {
                        var accepted = IsAccepted(message);
                        submission.TrySetResult(accepted);
                        if (!accepted)
                            log($"[{beneficiary}] RandomX share rejected: {message.Root.GetRawText()}");
                        continue;
                    }
                    if (string.Equals(message.Method, "job", StringComparison.Ordinal))
                    {
                        if (!authenticated)
                            throw new InvalidOperationException("RandomX pool sent work before login completed.");
                        Publish(RandomXJob.ParseNotification(message));
                    }
                }

                void Publish(RandomXJob job) => WorkReceived?.Invoke(new RandomXPoolWork(
                    this,
                    job,
                    new NonceAllocator(BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(sizeof(uint)))),
                    beneficiary));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                log($"[{beneficiary}] RandomX pool disconnected: {exception.Message}; retrying in {delay.TotalSeconds:g}s.");
                ConnectionLost?.Invoke(this);
                foreach (var pending in _submissions.Values)
                    pending.TrySetException(exception);
                endpointIndex++;
                if (exception is PoolAuthorizationException &&
                    !_firstConnection.Task.IsCompleted &&
                    endpointIndex >= _endpoints.Length)
                {
                    _firstConnection.TrySetException(new InvalidOperationException(
                        $"No configured {beneficiary.ToString().ToLowerInvariant()} RandomX pool accepted " +
                        "the wallet/worker login. Mining was not started and no destination substitution was made.",
                        exception));
                    return;
                }
            }
            finally
            {
                lock (_connectionGate)
                {
                    if (ReferenceEquals(_connection, connection))
                    {
                        _connection = null;
                        _activeOptions = null;
                        _sessionId = null;
                    }
                }
            }

            if (!cancellationToken.IsCancellationRequested)
                await Task.Delay(delay, cancellationToken);
        }
    }

    private static bool IsAccepted(StratumMessage message)
    {
        if (message.HasError || !message.Root.TryGetProperty("result", out var result))
            return false;
        if (result.ValueKind is JsonValueKind.True)
            return true;
        return result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("status", out var status) &&
            status.ValueKind == JsonValueKind.String &&
            string.Equals(status.GetString(), "OK", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_connectionTask is not null)
        {
            try { await _connectionTask; }
            catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
    }
}
