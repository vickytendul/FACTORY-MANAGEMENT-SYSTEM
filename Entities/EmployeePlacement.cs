namespace FactoryManagementSystem.Entities
{
    /// Where somebody ACTUALLY works, as a supervisor confirmed it.
    ///
    /// Payroll says a department and a designation; this says whether that
    /// is true, and where they really are if it is not. The two are kept
    /// side by side rather than one overwriting the other - payroll stays
    /// the record of what the company filed, this is the record of what the
    /// floor found, and the gap between them is the whole point.
    ///
    /// Nothing here is ever written back to the Company API. It is our data,
    /// in our database, about their data.
    public class EmployeePlacement
    {
        public string EmployeeCode { get; set; } = string.Empty;

        /// What payroll said AT THE MOMENT OF CONFIRMATION. Kept so a later
        /// payroll change can be spotted: if this no longer matches what the
        /// API returns, the confirmation was about a different posting and
        /// has to be made again.
        public string PayrollDepartment { get; set; } = string.Empty;
        public string PayrollDesignation { get; set; } = string.Empty;

        public string ActualDepartment { get; set; } = string.Empty;

        /// What they actually do, when the department alone does not say it.
        /// Free text on purpose - the floor's vocabulary is not payroll's.
        public string ActualWork { get; set; } = string.Empty;

        public string Remarks { get; set; } = string.Empty;

        public DateTime VerifiedOn { get; set; }
        public string VerifiedBy { get; set; } = string.Empty;
    }
}
