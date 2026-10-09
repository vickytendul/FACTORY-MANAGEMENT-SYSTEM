using FactoryManagementSystem.Entities;

namespace FactoryManagementSystem.Services.Layouts
{
    /// Why a layout write did not happen. The controllers turn these into
    /// the exact BadRequest/NotFound messages they already returned, so the
    /// API contract Flutter depends on is unchanged.
    public enum LayoutWriteStatus
    {
        Ok,
        TargetLayoutExists,
        SourceLayoutNotFound,
        AllocationsExist,
    }

    public sealed record LayoutCopyResult(LayoutWriteStatus Status, int Copied);

    public sealed record LayoutDeleteResult(LayoutWriteStatus Status, int Deleted);

    /// The SIX fields SyncLayoutAsync writes to an allocation that already
    /// exists. Deliberately not a whole row: the original code issues a
    /// partial Firestore UpdateAsync, so AllocationDate, AllocatedDateTime,
    /// AllocatedBy, OperationId and the rest must survive untouched. A
    /// full-row upsert here would quietly rewrite a row's allocation history.
    public sealed record AllocationFieldUpdate(
        string DocumentId,
        string EmployeeCode,
        string EmployeeBarcode,
        string EmployeeName,
        string EmployeeGrade,
        string Section,
        int LayoutNo);

    /// The FOUR fields SyncLayoutAsync clears on a row whose LayoutMaster
    /// no longer appears in the request. Section and LayoutNo are
    /// deliberately left alone - the original code does not touch them here.
    public sealed record AllocationEmployeeClear(string DocumentId);

    /// One line/CC/layout's worth of allocation changes, applied as a unit.
    ///
    /// Grouping them matters for more than tidiness. Supabase enforces
    /// `layout_tx_employee` - UNIQUE (employee_code) WHERE is_active AND
    /// employee_code &lt;&gt; '' - which Firestore has no equivalent of. Moving
    /// an operator from station A to station B on the same line means row B
    /// takes a code row A still holds, so applying the rows one at a time
    /// trips that index halfway through. The implementations clear every
    /// touched row's employee_code first and then write the new values, all
    /// inside one transaction, which is also what makes a failed save leave
    /// nothing half-written.
    public sealed record AllocationPlan(
        IReadOnlyList<AllocationFieldUpdate> Updates,
        IReadOnlyList<LayoutTransaction> Creates,
        IReadOnlyList<AllocationEmployeeClear> Clears)
    {
        public static AllocationPlan Empty { get; } = new(
            Array.Empty<AllocationFieldUpdate>(),
            Array.Empty<LayoutTransaction>(),
            Array.Empty<AllocationEmployeeClear>());

        public int TouchedCount => Updates.Count + Creates.Count + Clears.Count;
    }

    /// One row of a batch layout save, already resolved: the caller has
    /// decided this row's identity, its OperationId and its position, so
    /// both stores write exactly the same thing.
    public sealed record ResolvedMasterRow(
        string DocumentId,
        int Id,
        int CCId,
        int LayoutNo,
        int SNo,
        int OperationId,
        string OperationName,
        string OperationGrade,
        string MachineType,
        int DisplayOrder,
        string Section,
        bool IsActive,
        bool IsRequired = true)
    {
        public LayoutMaster ToEntity() => new()
        {
            IsRequired = IsRequired,
            Id = Id,
            CCId = CCId,
            LayoutNo = LayoutNo,
            SNo = SNo,
            OperationId = OperationId,
            OperationName = OperationName,
            OperationGrade = OperationGrade,
            MachineType = MachineType,
            DisplayOrder = DisplayOrder,
            Section = Section,
            IsActive = IsActive,
        };
    }

    /// A whole (CC, LayoutNo) master set, rewritten in place.
    ///
    /// `Writes` are the rows that should exist afterwards, in order.
    /// `DeleteDocumentIds` are the surplus rows the previous save left
    /// behind, which BatchSave hard-deletes - preserved as a hard delete
    /// rather than a soft one, because that is what the endpoint does today
    /// and DeleteLayout depends on rows actually disappearing.
    public sealed record MasterBatchPlan(
        int CCId,
        int LayoutNo,
        IReadOnlyList<ResolvedMasterRow> Writes,
        IReadOnlyList<string> DeleteDocumentIds);

    /// A LayoutMaster together with the store identity the caller needs to
    /// rewrite or delete it. LayoutMaster itself carries no document id -
    /// there is no [FirestoreDocumentId] on it - so the write paths, which
    /// must address individual rows, get it alongside.
    public sealed record IdentifiedMaster(string DocumentId, LayoutMaster Record);
}
