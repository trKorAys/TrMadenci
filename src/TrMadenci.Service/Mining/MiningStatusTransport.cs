using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrMadenci.Service.Mining;

internal static class MiningStatusTransport
{
    public const string Prefix = "@@TRMADENCI_STATUS_V1@@";

    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public static string Encode(MiningStatusSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static bool TryDecode(string line, out MiningStatusSnapshot? snapshot)
    {
        snapshot = null;
        if (!line.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        try
        {
            var payload = line[Prefix.Length..];
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            snapshot = JsonSerializer.Deserialize<MiningStatusSnapshot>(json, JsonOptions);
            return snapshot is not null;
        }
        catch (Exception exception) when (
            exception is FormatException or JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
