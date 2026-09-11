using System.Text.Json;

namespace TrMadenci.Protocols.Stratum;

public sealed class StratumMessage : IDisposable
{
    private readonly JsonDocument _document;

    private StratumMessage(JsonDocument document) => _document = document;

    public JsonElement Root => _document.RootElement;

    public int? Id =>
        Root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var value)
            ? value
            : null;

    public string? Method =>
        Root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String
            ? method.GetString()
            : null;

    public bool? BooleanResult =>
        Root.TryGetProperty("result", out var result) && result.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? result.GetBoolean()
            : null;

    public bool HasError =>
        Root.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    public static StratumMessage Parse(string jsonLine) => new(JsonDocument.Parse(jsonLine));

    public void Dispose() => _document.Dispose();
}
