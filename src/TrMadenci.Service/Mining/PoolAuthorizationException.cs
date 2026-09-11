namespace TrMadenci.Service.Mining;

internal sealed class PoolAuthorizationException(string message) : InvalidOperationException(message);
