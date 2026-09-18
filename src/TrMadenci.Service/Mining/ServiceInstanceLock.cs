using System.Text.Json;

namespace TrMadenci.Service.Mining;

internal sealed record ServiceInstanceMetadata(
    int ProcessId,
    string Role,
    DateTimeOffset StartedAt,
    string Executable);

internal sealed class ServiceInstanceLock : IDisposable
{
    public const string MiningRole = "mining";
    public const string SupervisorRole = "supervisor";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly FileStream? _stream;

    private ServiceInstanceLock(
        string lockPath,
        FileStream? stream,
        ServiceInstanceMetadata? owner)
    {
        LockPath = lockPath;
        _stream = stream;
        Owner = owner;
    }

    public bool Acquired => _stream is not null;
    public string LockPath { get; }
    public ServiceInstanceMetadata? Owner { get; }

    public static ServiceInstanceLock TryAcquire(string role, string? lockDirectory = null)
    {
        var normalizedRole = role switch
        {
            MiningRole => MiningRole,
            SupervisorRole => SupervisorRole,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown instance-lock role.")
        };
        var directory = lockDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TrMadenci",
            "run");
        Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(directory, normalizedRole + ".lock");

        FileStream stream;
        try
        {
            stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough);
        }
        catch (IOException)
        {
            return new ServiceInstanceLock(lockPath, null, TryReadOwner(lockPath));
        }

        var owner = new ServiceInstanceMetadata(
            Environment.ProcessId,
            normalizedRole,
            DateTimeOffset.Now,
            Environment.ProcessPath ?? "unknown");
        try
        {
            stream.SetLength(0);
            JsonSerializer.Serialize(stream, owner, JsonOptions);
            stream.Flush(flushToDisk: true);
            stream.Position = 0;
            return new ServiceInstanceLock(lockPath, stream, owner);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose() => _stream?.Dispose();

    private static ServiceInstanceMetadata? TryReadOwner(string lockPath)
    {
        try
        {
            using var stream = new FileStream(
                lockPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<ServiceInstanceMetadata>(stream);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
