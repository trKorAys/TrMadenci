using System.Collections.Concurrent;
using TrMadenci.Core.Configuration;
using TrMadenci.Core.Fees;
using TrMadenci.Protocols.Stratum;

namespace TrMadenci.Service.Mining;

internal sealed record KawPowPoolWork(
    KawPowPoolClient Pool,
    KawPowJob Job,
    byte[] Target,
    NonceAllocator Nonces,
    MiningBeneficiary Beneficiary);

internal sealed class NonceAllocator(ulong initialValue)
{
    private readonly object _gate = new();
    private ulong _next = initialValue;

    public ulong Reserve(uint count)
    {
        lock (_gate)
        {
            var result = _next;
            _next = unchecked(_next + count);
            return result;
        }
    }
}

internal sealed class KawPowPoolClient(
    PoolOptions options,
    MiningBeneficiary beneficiary,
    Action<string> log,
    PoolOptions? failover = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _submissions = new();
    private readonly TaskCompletionSource _firstConnection = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _connectionGate = new();
    private readonly PoolOptions[] _endpoints = failover is null ? [options] : [options, failover];
    private StratumConnection? _connection;
    private PoolOptions? _activeOptions;
    private Task? _connectionTask;
    private int _nextRequestId = 100;

    public event Action<KawPowPoolWork>? WorkReceived;
    public event Action<KawPowPoolClient>? ConnectionLost;

    public string Endpoint => $"{(_activeOptions ?? options).Host}:{(_activeOptions ?? options).Port}";
    public string Username => (_activeOptions ?? options).Username;
    public MiningBeneficiary Beneficiary => beneficiary;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _connectionTask ??= Task.Run(() => ConnectionLoopAsync(_lifetime.Token), CancellationToken.None);
        await _firstConnection.Task.WaitAsync(cancellationToken);
    }

    public async Task<bool> SubmitAsync(KawPowJob job, CudaShare share, CancellationToken cancellationToken)
    {
        StratumConnection connection;
        PoolOptions activeOptions;
        lock (_connectionGate)
        {
            connection = _connection ?? throw new InvalidOperationException($"{beneficiary} pool is reconnecting.");
            activeOptions = _activeOptions ?? options;
        }

        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_submissions.TryAdd(id, completion))
            throw new InvalidOperationException("Could not allocate a Stratum submission id.");
        try
        {
            await connection.SendAsync(StratumRequest.SubmitKawPow(
                id, activeOptions.Username, job.JobId, share.Nonce, job.HeaderHash, share.MixHash), cancellationToken);
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
                }
                await connection.SendAsync(StratumRequest.Subscribe(1, "TrMadenci/0.2.0"), cancellationToken);
                await connection.SendAsync(StratumRequest.Authorize(2, currentOptions.Username, currentOptions.Password), cancellationToken);

                KawPowSubscription? subscription = null;
                byte[]? target = null;
                var authorized = false;
                while (!cancellationToken.IsCancellationRequested)
                {
                    using var message = await connection.ReadAsync(cancellationToken);
                    if (message.Id == 1)
                    {
                        subscription = KawPowSubscription.Parse(message);
                        if (authorized)
                            MarkReady();
                        continue;
                    }
                    if (message.Id == 2)
                    {
                        if (message.BooleanResult != true || message.HasError)
                            throw new PoolAuthorizationException(
                                $"Pool authorization rejected for {currentOptions.Username}.");
                        authorized = true;
                        if (subscription is not null)
                            MarkReady();
                        continue;
                    }
                    if (message.Id is { } responseId && _submissions.TryRemove(responseId, out var submission))
                    {
                        var accepted = message.BooleanResult == true && !message.HasError;
                        submission.TrySetResult(accepted);
                        if (!accepted)
                            log($"[{beneficiary}] share rejected: {message.Root.GetRawText()}");
                        continue;
                    }
                    if (string.Equals(message.Method, "mining.set_target", StringComparison.Ordinal))
                    {
                        target = StratumTarget.Parse(message).Bytes;
                        continue;
                    }
                    if (string.Equals(message.Method, "mining.notify", StringComparison.Ordinal))
                    {
                        var currentSubscription = subscription ??
                            throw new InvalidOperationException("Pool sent work before the extranonce subscription.");
                        var job = KawPowJob.Parse(message);
                        WorkReceived?.Invoke(new KawPowPoolWork(
                            this,
                            job,
                            (target ?? job.Target).ToArray(),
                            new NonceAllocator(currentSubscription.NonceBase),
                            beneficiary));
                    }
                }

                void MarkReady()
                {
                    log($"[{beneficiary}] pool ready: {Endpoint} / {Username}");
                    _firstConnection.TrySetResult();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                log($"[{beneficiary}] pool disconnected: {exception.Message}; retrying in 5s.");
                ConnectionLost?.Invoke(this);
                foreach (var pending in _submissions.Values)
                    pending.TrySetException(exception);
                endpointIndex++;
                if (exception is PoolAuthorizationException &&
                    !_firstConnection.Task.IsCompleted &&
                    endpointIndex >= _endpoints.Length)
                {
                    _firstConnection.TrySetException(new InvalidOperationException(
                        $"No configured {beneficiary.ToString().ToLowerInvariant()} pool accepted worker authorization. " +
                        "Mining was not started and no destination substitution was made.", exception));
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
                    }
                }
            }

            if (!cancellationToken.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
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
