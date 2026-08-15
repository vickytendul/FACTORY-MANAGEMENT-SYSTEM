using System.Text.Json.Serialization;

namespace FactoryManagementSystem.Entities
{
    /// One employee record from the Company payroll API
    /// (http://life.gainup.in:8089/api/payroll/Employee_Att) - fixed fields
    /// only. The sync process has no use for the dynamic date-keyed
    /// attendance fields also present on this payload (a separate, future
    /// phase), so they are simply not modeled here and ignored during
    /// deserialization.
    public class CompanyApiEmployee
    {
        [JsonPropertyName("tno")]
        public string? Tno { get; set; }

        [JsonPropertyName("Name")]
        public string? Name { get; set; }

        [JsonPropertyName("DeptName")]
        public string? DeptName { get; set; }

        [JsonPropertyName("DesignationName")]
        public string? DesignationName { get; set; }

        [JsonPropertyName("Unit")]
        public string? Unit { get; set; }

        [JsonPropertyName("Sex")]
        public string? Sex { get; set; }

        [JsonPropertyName("Contact")]
        public string? Contact { get; set; }

        [JsonPropertyName("DateOfReleave")]
        public string? DateOfReleave { get; set; }

        [JsonPropertyName("Exepereince")]
        public double? Experience { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        /// Not yet part of the Company API contract - re-verified live
        /// during Phase 9A/10 (the response has no barcode-like field
        /// today). No [JsonPropertyName] is mapped here on purpose:
        /// guessing a key name is exactly what was ruled out when this
        /// field was prepared. Wire the real key up the moment the vendor
        /// confirms it - nothing else needs to change.
        [JsonIgnore]
        public string? Barcode { get; set; }
    }
}
