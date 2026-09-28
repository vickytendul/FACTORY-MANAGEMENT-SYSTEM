using FactoryManagementSystem.Entities;

namespace FactoryManagementSystem.Services.Attendance
{
    /// Every AttendanceTransaction read and write, behind one interface so
    /// the store can be swapped without the controllers - or the JSON they
    /// return - noticing.
    ///
    /// Identity is the Firestore DOCUMENT id, carried on
    /// AttendanceTransaction.FirestoreId. AttendanceId is 0 on every row in
    /// Firebase and is deliberately not stored, exactly as TransactionId is
    /// not stored for layout rows.
    public interface IAttendanceRepository
    {
        /// Attendance for one line/CC/date. Cached on the Firestore path.
        Task<List<AttendanceTransaction>> GetForLineDateAsync(int lineId, int ccId, DateTime utcDate);

        /// The whole factory's attendance for one date. Cached on the
        /// Firestore path.
        Task<List<AttendanceTransaction>> GetForDateAsync(DateTime utcDate);

        /// Rows on ANY line for that date naming one of these employees as
        /// the replacement - who is standing in for somebody today.
        Task<List<AttendanceTransaction>> GetByReplacementCodesAsync(
            DateTime utcDate, IEnumerable<string> replacementCodes);

        /// One line's rows across a range of dates. Used by the Line Summary
        /// "borrowed in" half.
        Task<List<AttendanceTransaction>> GetForLineDatesAsync(int lineId, IEnumerable<DateTime> utcDates);

        /// Rows on any line and any date naming one of these employees as
        /// the replacement. The Line Summary "lent out" half; deliberately
        /// unbounded by date, matching the query it replaces.
        Task<List<AttendanceTransaction>> GetByReplacementCodesAllDatesAsync(
            IEnumerable<string> replacementCodes);

        void InvalidateCache();

        /// Applies one line/CC/date's attendance as a unit: rows that
        /// already exist are updated, the rest are created. Returns how many
        /// rows were touched.
        ///
        /// `allowCreate` is the Save/Update distinction the controller
        /// already makes - Update must refuse to invent a row that is not
        /// there, and says which employee was missing.
        Task<int> ApplyAsync(AttendancePlan plan, bool allowCreate);
    }

    /// One line/CC/date's worth of attendance, resolved by the caller.
    ///
    /// Grouped rather than applied row by row for the same reason the layout
    /// allocation plan is: the whole save is one all-or-nothing unit, so a
    /// failure partway cannot leave half a line marked.
    public sealed record AttendancePlan(
        int LineId,
        int CCId,
        DateTime AttendanceDate,
        IReadOnlyList<AttendanceTransaction> Rows);
}
