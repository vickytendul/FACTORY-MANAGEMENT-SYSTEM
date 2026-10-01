using FactoryManagementSystem.Entities;

namespace FactoryManagementSystem.Services.Placements
{
    /// When a confirmation is still believed.
    ///
    /// Lives here rather than in a controller because two of them ask the
    /// question - the confirm screen, to colour a row, and the Strength
    /// Summary, to count it - and two copies of this would be two chances
    /// for the screens to disagree about who is confirmed.
    public static class PlacementRules
    {
        /// Somebody confirmed in March may have moved by October. Without
        /// an expiry the count would read well and mean nothing a year on,
        /// which is worse than no count at all because it looks believable.
        public const int DefaultVerifyWithinDays = 90;

        public static int VerifyWithinDays(IConfiguration configuration) =>
            int.TryParse(configuration["Placements:VerifyWithinDays"], out var d) && d > 0
                ? d
                : DefaultVerifyWithinDays;

        /// Payroll has re-filed them since the confirmation was made, so it
        /// was about a posting they no longer hold - out of date however
        /// recently it was given.
        public static bool PayrollChanged(
            EmployeePlacement placement, string payrollDepartment, string payrollDesignation) =>
            !string.Equals(placement.PayrollDepartment, payrollDepartment,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(placement.PayrollDesignation, payrollDesignation,
                StringComparison.OrdinalIgnoreCase);

        /// Recent enough, and about the posting they still hold.
        public static bool IsFresh(
            EmployeePlacement placement, string payrollDepartment, string payrollDesignation,
            DateTime cutoff) =>
            placement.VerifiedOn.Date >= cutoff.Date
            && !PayrollChanged(placement, payrollDepartment, payrollDesignation);
    }
}
