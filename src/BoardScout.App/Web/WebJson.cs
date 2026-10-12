using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoardScout.Views;

internal static class Json
{
    /// <summary>The web views' payloads: the shared models name their own fields; the Linux edition's records use camelCase.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Wraps a payload as a message: {"type":"…","data":…}.</summary>
    public static string Message(string type, string dataJson) => $"{{\"type\":{JsonSerializer.Serialize(type)},\"data\":{dataJson}}}";
}
