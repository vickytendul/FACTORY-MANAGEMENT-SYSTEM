namespace FactoryManagementSystem.Services.Layouts
{
    /// Which table of the allocation screen a layout row stands in.
    ///
    /// The Layout Master offers MAIN, SUPER TEAM, four named support
    /// sections - SEWING HELPER, LINE LEADER, CHECKERS, PACKING HELPER -
    /// and the catch-all OTHERS. The allocation screen draws four tables
    /// from those: MAIN, OTHERS (the named four), BACKUP - SUPER TEAM, and
    /// BACKUP - OTHERS, which is the catch-all and is the bench.
    ///
    /// The rule lives here rather than in each query so the Home Screen
    /// card and the allocation screen cannot come to disagree about what
    /// "OTHERS" means - a card reading 8 / 8 beside a table of nine is
    /// worse than no card at all. Its twin on the client is
    /// select_zone_page.dart; if one changes, change both.
    public static class LayoutSections
    {
        public const string Main = "MAIN";
        public const string SuperTeam = "SUPER TEAM";

        /// The catch-all, which the screen prints as BACKUP - OTHERS.
        public const string Bench = "OTHERS";

        /// Operations that stand in the OTHERS table whatever section they
        /// were filed under.
        ///
        /// QC and IE have no section of their own, so whoever laid the
        /// line out had to file them under the catch-all and they came out
        /// on the bench. They are not waiting for work: they stand on the
        /// line doing a job, exactly like a checker or a line leader.
        public static readonly string[] OthersOperations = { "QC", "IE" };

        private static string Norm(string? s) => (s ?? string.Empty).Trim().ToUpperInvariant();

        public static bool IsMain(string? section) => Norm(section) == Main;

        /// Everything the OTHERS table holds: the named support sections,
        /// plus a QC or IE filed under the catch-all.
        public static bool IsOthers(string? section, string? operationName)
        {
            var sec = Norm(section);
            if (sec == Main || sec == SuperTeam) return false;
            if (sec != Bench) return true;
            return OthersOperations.Contains(Norm(operationName));
        }
    }
}
