using System.Collections.Concurrent;
using System.Security.Cryptography;
using TrMadenci.Core.Algorithms;
using TrMadenci.Core.Configuration;
using TrMadenci.Core.Fees;
using TrMadenci.Protocols.Stratum;

namespace TrMadenci.Service.Mining;

internal sealed record OctopusPoolWork(
    OctopusPoolClient Pool,
    OctopusJob Job,
    int EpochNumber,
    byte[] Target,
    NonceAllocator Nonces,
    MiningBeneficiary Beneficiary);

internal sealed class OctopusPoolClient(
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
    private Task? _connectionTask;
    private int _nextRequestId = 100;

    public event Action<OctopusPoolWork>? WorkReceived;
    public event Action<OctopusPoolClient>? ConnectionLost;

    public string Endpoint => $"{(_activeOptions ?? options).Host}:{(_activeOptions ?? options).Port}";
    public string Username => (_activeOptions ?? options).Username;
    public MiningBeneficiary Beneficiary => beneficiary;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _connectionTask ??= Task.Run(() => ConnectionLoopAsync(_lifetime.Token), CancellationToken.None);
        await _firstConnection.Task.WaitAsync(cancellationToken);
    }

    public async Task<bool> SubmitAsync(OctopusJob job, CudaShare share, CancellationToken cancellationToken)
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
            await connection.SendAsync(StratumRequest.SubmitOctopus(
                id, activeOptions.Username, job.JobId, share.Nonce, job.HeaderHash), cancellationToken);
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
                await connection.SendAsync(StratumRequest.SubscribeOctopus(
                    1, currentOptions.Username, currentOptions.Password), cancellationToken);

                var subscribed = false;
                while (!cancellationToken.IsCancellationRequested)
                {
                    using var message = await connection.ReadAsync(cancellationToken);
                    if (message.Id == 1)
                    {
                        if (message.BooleanResult != true || message.HasError)
                            throw new PoolAuthorizationException(
                                $"Octopus pool authorization rejected for {currentOptions.Username}.");
                        subscribed = true;
                        log($"[{beneficiary}] Octopus pool ready: {Endpoint} / {Username}");
                        _firstConnection.TrySetResult();
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
                    if (string.Equals(message.Method, "mining.notify", StringComparison.Ordinal))
                    {
                        if (!subscribed)
                            throw new InvalidOperationException("Octopus pool sent work before authorization succeeded.");
                        var job = OctopusJob.Parse(message);
                        var epoch = OctopusParameters.GetEpoch(job.BlockHeight);
                        WorkReceived?.Invoke(new OctopusPoolWork(
                            this,
                            job,
                            epoch.EpochNumber,
                            job.Boundary.ToArray(),
                            new NonceAllocator(RandomNonce()),
                            beneficiary));
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                log($"[{beneficiary}] Octopus pool disconnected: {exception.Message}; retrying in 5s.");
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
                await Task.Delay(reconnectDelay ?? TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private static ulong RandomNonce() => BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(sizeof(ulong)));

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
