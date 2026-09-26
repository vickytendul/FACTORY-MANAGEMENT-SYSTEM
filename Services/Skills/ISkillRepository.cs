using FactoryManagementSystem.Entities;

namespace FactoryManagementSystem.Services.Skills
{
    /// Every read and write SkillTransactionController performs against
    /// skill records, behind one interface so the store can be swapped
    /// without the controller - or the API contract, or Flutter - noticing.
    ///
    /// Deliberately shaped around what the controller already asks for
    /// rather than around either database. The Firestore implementation is
    /// today's code moved, not rewritten, so "switch back" stays a config
    /// change rather than a revert.
    public interface ISkillRepository
    {
        /// Active records, optionally narrowed. Backs GET api/SkillTransaction.
        Task<List<SkillTransaction>> GetActiveAsync(string? employeeCode = null, int? ccId = null);

        /// One active record by its app-level TransactionId, or null.
        Task<SkillTransaction?> GetByTransactionIdAsync(int transactionId);

        /// Every active record. Backs the backup report and the operators
        /// summary, which both need the whole set.
        Task<List<SkillTransaction>> GetAllActiveAsync();

        /// Active records for one operation, matched by OperationId OR by
        /// normalised name - the same two-way match the roster has always
        /// needed, because one operation carries different ids across
        /// layouts (154 distinct names against 253 distinct ids, measured).
        Task<List<SkillTransaction>> GetForOperationAsync(int operationId, string normalizedOperationName);

        /// Active records for one operation id only. Backs backup-candidates.
        Task<List<SkillTransaction>> GetByOperationIdAsync(int operationId);

        /// Creates, or updates the existing active record with the same
        /// natural key (EmployeeCode + OperationName + MachineType +
        /// OperationGrade + Section + CCId). Returns the stored record and
        /// whether it was newly created.
        Task<(SkillTransaction Record, bool Created)> SaveAsync(SkillTransaction request);

        /// Updates one record found by TransactionId. Returns null when
        /// there is no active record with that id.
        Task<SkillTransaction?> UpdateAsync(int transactionId, SkillTransaction request);

        /// Soft delete - IsActive false, never a hard delete. False when no
        /// active record carried that id.
        Task<bool> SoftDeleteAsync(int transactionId);
    }
}
