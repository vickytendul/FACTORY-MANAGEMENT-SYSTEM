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

        /// PHASE 12D - the Company API now provides this field as
        /// "Bar_Code" (confirmed via a live fetch), so it's wired up here
        /// the same way as every other fixed field above.
        [JsonPropertyName("Bar_Code")]
        public string? Barcode { get; set; }
    }
}
