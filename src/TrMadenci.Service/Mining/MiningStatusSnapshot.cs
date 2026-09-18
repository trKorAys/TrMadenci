using TrMadenci.Core.Fees;
using TrMadenci.NativeBridge;

namespace TrMadenci.Service.Mining;

internal sealed record GpuMiningStatus(
    int DeviceIndex,
    bool IsPreparing,
    double HashesPerSecond,
    GpuTelemetry? Telemetry,
    string? DeviceLabel = null,
    ComputeWorkerPhase WorkerPhase = ComputeWorkerPhase.Paused,
    long RecoveryCount = 0,
    string? LastWorkerError = null);

internal sealed record MiningStatusSnapshot(
    DateTimeOffset Timestamp,
    TimeSpan SessionElapsed,
    TimeSpan ActiveMiningTime,
    string CoinName,
    string CoinTicker,
    string Network,
    string Algorithm,
    string Pool,
    MiningBeneficiary Beneficiary,
    double CurrentHashesPerSecond,
    double AverageHashesPerSecond,
    ulong TotalHashes,
    long AcceptedShares,
    long RejectedShares,
    long InvalidShares,
    double SessionEnergyKwh,
    double? AveragePowerWatts,
    IReadOnlyList<GpuMiningStatus> Gpus,
    bool IsManuallyPaused = false,
    long AcceptedUserShares = 0,
    long AcceptedDeveloperShares = 0);
