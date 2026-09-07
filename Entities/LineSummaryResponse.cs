namespace FactoryManagementSystem.Entities
{
    /// Real production response for the Line Summary page - one Line, one
    /// date. Manpower fields (On Roll/Present/Absent) come from
    /// LayoutTransactions (the Line/Section/CC mapping) cross-referenced
    /// against the Company API Employee_Att response (attendance) for the
    /// selected date; SAM comes from the CC document; Output/Rej come from
    /// the Company API SewingProdRept response. See LineSummaryController
    /// for exactly which source feeds which field.
    ///
    /// Deliberately excludes: Replacement (no data source once
    /// AttendanceTransactions is out of scope - the frontend always shows
    /// this metric as unavailable, independent of this response).
    ///
    /// WorkingMinutes/AvailableMinutesOwe/AvailableMinutesEff/EarnedMinutes/
    /// OwePercent/EffPercent below ARE implemented. No named formula for
    /// Working/Available Minutes ever existed anywhere in this codebase
    /// (not even in DashboardController's dead code - only an unlabeled
    /// inline `presentCount * 480` expression, never a returned field), so
    /// these are derived consistently from the one named formula that DOES
    /// exist there (Output*SAM / owe / efficiency), using the same
    /// per-employee-per-day constant (480) and this response's own already-
    /// computed TotalPresent/TailorsPresent - never a new Firestore read or
    /// Company API call. Under this derivation, WorkingMinutes and
    /// AvailableMinutesOwe are the same value (TotalPresent * 480) - the
    /// "available" and "working" minute pools coincide when there is no
    /// separate loss/adjustment factor defined anywhere in the codebase.
    public class LineSummaryResponse
    {
        // Same constant as DashboardController.WorkingMinutesPerDay - the
        // one existing business constant this formula depends on.
        private const double WorkingMinutesPerDay = 480;

        /// Only populated by LineSummaryController.GetRange (one entry per
        /// day in the requested range) - null/unused for the single-day Get
        /// action, which already carries an explicit date in its own
        /// request instead.
        public DateTime? Date { get; set; }

        public string CCNo { get; set; } = string.Empty;

        /// Null when the CC document itself could not be resolved - the
        /// frontend must show this as unavailable, never as 0.
        public double? SAM { get; set; }

        public int TotalPositions { get; set; }

        public int TailorsOnRoll { get; set; }
        public int OthersOnRoll { get; set; }
        public int TotalOnRoll { get; set; }

        public int TailorsPresent { get; set; }
        public int OthersPresent { get; set; }
        public int TotalPresent { get; set; }

        public int Absent { get; set; }

        /// On-roll employees whose attendance for the selected date could
        /// not be determined from the Company API response - either the
        /// EmployeeCode did not appear in that day's response at all, or it
        /// appeared with a status other than Present/Absent (e.g. a leave
        /// code). Never folded into Present or Absent - see
        /// LineSummaryController.ClassifyAttendance for the exact rule.
        public int UnknownAttendance { get; set; }

        public decimal Absenteeism =>
            TotalOnRoll == 0
                ? 0
                : Math.Round((decimal)Absent * 100 / TotalOnRoll, 2);

        public double Output { get; set; }
        public double Rej { get; set; }

        /// TotalPresent * 480 - the per-employee-per-day working-minute
        /// pool for whoever is actually present on this line today.
        /// TotalPresent is always a real, already-computed int (never
        /// null), so this is never null in practice - typed nullable only
        /// for contract symmetry with the other five metrics below.
        public double? WorkingMinutes => TotalPresent * WorkingMinutesPerDay;

        /// Same formula/value as WorkingMinutes under this derivation - see
        /// the class remarks above for why the two coincide here.
        public double? AvailableMinutesOwe => TotalPresent * WorkingMinutesPerDay;

        /// Same as AvailableMinutesOwe but against TailorsPresent only.
        public double? AvailableMinutesEff => TailorsPresent * WorkingMinutesPerDay;

        /// Output x SAM - the same per-line term DashboardController.
        /// GetOriginal() already sums factory-wide (x.Output * cc.SAM).
        /// Null (never a fabricated 0) whenever SAM itself is unavailable.
        /// A real Output of 0 with a real SAM still yields a real 0 here -
        /// never treated as unavailable.
        public double? EarnedMinutes =>
            SAM == null ? null : Math.Round(Output * SAM.Value, 2);

        /// EarnedMinutes / AvailableMinutesOwe * 100 - the same formula
        /// DashboardController.GetOriginal() uses for its factory-wide
        /// "owe" value, applied per line. Null whenever EarnedMinutes is
        /// unavailable or the denominator is 0 (no one present to divide
        /// by) - never 0, NaN, or Infinity. No ceiling is applied - a
        /// genuinely tiny present count can mathematically produce a
        /// percentage far above 100%, and that real value is shown as-is.
        public double? OwePercent =>
            EarnedMinutes == null || AvailableMinutesOwe is null or 0
                ? null
                : Math.Round(EarnedMinutes.Value / AvailableMinutesOwe.Value * 100, 2);

        /// Same as OwePercent but against AvailableMinutesEff - mirrors
        /// DashboardController.GetOriginal()'s "efficiency" value.
        public double? EffPercent =>
            EarnedMinutes == null || AvailableMinutesEff is null or 0
                ? null
                : Math.Round(EarnedMinutes.Value / AvailableMinutesEff.Value * 100, 2);
    }
}
