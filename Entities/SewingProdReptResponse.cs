using System.Text.Json;
using System.Text.Json.Serialization;

namespace FactoryManagementSystem.Entities
{
    /// One row of the Company API's Sewing Production Report response
    /// (POST http://life.gainup.in:8089/api/woven/SewingProdRept).
    ///
    /// The response mixes a small set of fixed fields (EffectFrom, Type,
    /// Slno, Total) with dynamic, operation-number-keyed fields ("1"
    /// through "56", "101", "105"). A C# property can never be named "1",
    /// so - mirroring the exact "fixed fields vs. everything else" bucket
    /// pattern the frontend's CompanyEmployee model already uses for the
    /// Employee API's date-keyed attendance fields - every key that isn't
    /// one of the fixed fields is captured verbatim in [Operations], keyed
    /// by the exact original operation-number string. Nothing is renamed,
    /// reordered, or dropped.
    [JsonConverter(typeof(SewingProdReptResponseConverter))]
    public class SewingProdReptResponse
    {
        public string? EffectFrom { get; set; }
        public string? Type { get; set; }
        public int? Slno { get; set; }
        public decimal Total { get; set; }

        /// Keyed by the exact operation-number string ("1", "2", ...,
        /// "56", "101", "105") exactly as the Company API sent it.
        public Dictionary<string, decimal> Operations { get; set; } = new();

        /// Safe accessor: a missing operation number becomes 0 rather than
        /// requiring a null/ContainsKey check at every call site.
        public decimal GetOperation(string operationNumber) =>
            Operations.TryGetValue(operationNumber, out var value) ? value : 0;
    }

    /// Reads every JSON property of one report row: the four fixed fields
    /// go to their named properties, everything else goes into
    /// [SewingProdReptResponse.Operations] unchanged. Writing mirrors this
    /// exactly, so a re-serialized row is a faithful round-trip of what
    /// the vendor sent (used when this app's own proxy endpoint returns
    /// the deserialized report back out as JSON).
    internal class SewingProdReptResponseConverter : JsonConverter<SewingProdReptResponse>
    {
        public override SewingProdReptResponse Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            var result = new SewingProdReptResponse();

            foreach (var prop in root.EnumerateObject())
            {
                if (string.Equals(prop.Name, "EffectFrom", StringComparison.OrdinalIgnoreCase))
                {
                    result.EffectFrom = ReadString(prop.Value);
                }
                else if (string.Equals(prop.Name, "Type", StringComparison.OrdinalIgnoreCase))
                {
                    result.Type = ReadString(prop.Value);
                }
                else if (string.Equals(prop.Name, "Slno", StringComparison.OrdinalIgnoreCase))
                {
                    result.Slno = prop.Value.ValueKind == JsonValueKind.Number ? prop.Value.GetInt32() : null;
                }
                else if (string.Equals(prop.Name, "Total", StringComparison.OrdinalIgnoreCase))
                {
                    result.Total = ReadDecimal(prop.Value);
                }
                else
                {
                    // Every remaining key - "1".."56", "101", "105", and
                    // any future operation number the vendor adds - is
                    // preserved exactly, verbatim, in Operations.
                    result.Operations[prop.Name] = ReadDecimal(prop.Value);
                }
            }

            return result;
        }

        public override void Write(Utf8JsonWriter writer, SewingProdReptResponse value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            if (value.EffectFrom != null) writer.WriteString("EffectFrom", value.EffectFrom); else writer.WriteNull("EffectFrom");
            if (value.Type != null) writer.WriteString("Type", value.Type); else writer.WriteNull("Type");
            if (value.Slno.HasValue) writer.WriteNumber("Slno", value.Slno.Value); else writer.WriteNull("Slno");
            foreach (var kv in value.Operations)
            {
                writer.WriteNumber(kv.Key, kv.Value);
            }
            writer.WriteNumber("Total", value.Total);
            writer.WriteEndObject();
        }

        private static string? ReadString(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.ToString(),
        };

        private static decimal ReadDecimal(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0,
        };
    }
}
