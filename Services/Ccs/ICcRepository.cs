using FactoryManagementSystem.Entities;

namespace FactoryManagementSystem.Services.Ccs
{
    /// Every CC read and write, behind one interface so the store can be
    /// swapped without CCsController or the JSON it returns noticing.
    ///
    /// Shaped around what the controller already asks for. The CC id itself
    /// still comes from the Firestore CCCounter in every mode - see
    /// ICcRepository.ReserveCcIdAsync - for the same reason the layout
    /// allocator does: one allocator means an id identifies the same CC in
    /// both stores, in both directions, for as long as both exist.
    public interface ICcRepository
    {
        /// Active CCs, cached. The accessor five consumers share.
        Task<List<CC>> GetActiveAsync();

        /// Every CC, active and inactive, ordered by CCNo. Only the
        /// includeInactive listing needs this.
        Task<List<CC>> GetAllAsync();

        /// One CC by its business id, or null.
        Task<CC?> GetByIdAsync(int ccId);

        /// The CC holding this number, or null. Used for the duplicate
        /// check, so it must see inactive rows too - a number in use by a
        /// deactivated CC is still taken.
        Task<CC?> FindByNumberAsync(string ccNo);

        void InvalidateCache();

        /// Reserves the next CC id. Firestore-backed in every mode.
        Task<int> ReserveCcIdAsync();

        /// Creates a CC under an id the caller has already reserved.
        Task CreateAsync(CC cc);

        /// Replaces number, SAM, active flag and multi-layout flag on one
        /// CC. Returns false when the CC does not exist.
        Task<bool> UpdateAsync(int ccId, string ccNo, double sam, bool isActive, bool hasMultipleLayouts);

        /// Flips IsActive. Returns the new value, or null when not found.
        Task<bool?> ToggleActiveAsync(int ccId);

        /// Sets SAM alone. Returns false when the CC does not exist.
        Task<bool> UpdateSamAsync(int ccId, double sam);
    }
}
