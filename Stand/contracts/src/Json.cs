using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stand.Contracts {
    public static class Json {
        public static readonly JsonSerializerOptions Line = new JsonSerializerOptions {
            IncludeFields = true,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() }
        };

        public static readonly JsonSerializerOptions Pretty = new JsonSerializerOptions {
            IncludeFields = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() }
        };

        // report.json: camelCase names, like the reports of the other stand modules.
        public static readonly JsonSerializerOptions Public = new JsonSerializerOptions {
            IncludeFields = true,
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() }
        };

        public static string SerializePublic<T>(T value) => JsonSerializer.Serialize(value, Public);

        public static string Serialize<T>(T value, bool pretty = false) => JsonSerializer.Serialize(value, pretty ? Pretty : Line);

        public static T Deserialize<T>(string text) => JsonSerializer.Deserialize<T>(text, Line);

        public static void WriteFile<T>(string path, T value) {
            File.WriteAllText(path, Serialize(value, true));
        }

        public static T ReadFile<T>(string path) => Deserialize<T>(File.ReadAllText(path));
    }

    /// <summary> One line of cases.ndjson. </summary>
    public sealed class CaseLine {
        public CaseResult Result;
        public ReproMaterial Repro;
    }
}
