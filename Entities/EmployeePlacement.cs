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

        /// Where they are now, and what they are doing now. Both, because
        /// the two come apart in both directions: somebody can sit in the
        /// right department doing a different job, and somebody can keep
        /// their job while sitting in another department. Checking only one
        /// would miss half of what the GM asked about.
        public string CurrentDepartment { get; set; } = string.Empty;
        public string CurrentDesignation { get; set; } = string.Empty;

        public string Remarks { get; set; } = string.Empty;

        public DateTime VerifiedOn { get; set; }
        public string VerifiedBy { get; set; } = string.Empty;
    }
}
