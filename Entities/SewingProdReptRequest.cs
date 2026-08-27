using System.Text.Json.Serialization;

namespace FactoryManagementSystem.Entities
{
    /// Request body for the Company API's Sewing Production Report
    /// (POST http://life.gainup.in:8089/api/woven/SewingProdRept) -
    /// verified in Postman. Field names/casing mirror the vendor's
    /// contract exactly, including the underscores in Line_No/CC_No/
    /// Unit_Code - this is also the exact shape our own proxy endpoint
    /// expects, so no field mapping/renaming happens anywhere in between.
    ///
    /// FDate/TDate are sent to the vendor exactly as provided (e.g.
    /// "01-aug-2026") - this DTO never parses or reformats them.
    public class SewingProdReptRequest
    {
        [JsonPropertyName("FDate")]
        public string FDate { get; set; } = string.Empty;

        [JsonPropertyName("TDate")]
        public string TDate { get; set; } = string.Empty;

        [JsonPropertyName("Line_No")]
        public int Line_No { get; set; }

        [JsonPropertyName("CC_No")]
        public string CC_No { get; set; } = string.Empty;

        [JsonPropertyName("Unit_Code")]
        public int Unit_Code { get; set; }
    }
}
