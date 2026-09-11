using System.Text.Json;
using TrMadenci.Core.Fees;

namespace TrMadenci.Service.Mining;

internal static class DeveloperFeeStateStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrMadenci");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "developer-fee-state.json");

    public static DeveloperFeeState? Load(Action<string> log)
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<DeveloperFeeState>(File.ReadAllText(FilePath))
                : null;
        }
        catch (Exception exception)
        {
            log($"Developer fee state could not be restored: {exception.Message}");
            return null;
        }
    }

    public static async Task SaveAsync(DeveloperFeeState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporaryPath = FilePath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath, JsonSerializer.Serialize(state), cancellationToken);
        File.Move(temporaryPath, FilePath, overwrite: true);
    }
}
