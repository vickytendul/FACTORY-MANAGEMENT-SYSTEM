using FactoryManagementSystem.Entities;
using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Services.Employees
{
    /// The Firestore employee master, as the controllers have always read
    /// it - including the 45 second roster cache, which is what keeps Skill
    /// Update's search from costing a Firestore read per keystroke.
    public sealed class FirestoreEmployeeRepository : IEmployeeRepository
    {
        private readonly FirestoreService _firestore;

        public FirestoreEmployeeRepository(FirestoreService firestore)
        {
            _firestore = firestore;
        }

        public Task<List<EmployeeMaster>> GetAllAsync() =>
            _firestore.GetAllEmployeesAsync();

        public async Task<EmployeeMaster?> FindByCodeAsync(string code)
        {
            var snapshot = await _firestore.EmployeeMasters
                .WhereEqualTo(nameof(EmployeeMaster.EmployeeCode), code)
                .Limit(1)
                .GetSnapshotAsync();

            var document = snapshot.Documents.FirstOrDefault();
            return document?.ConvertTo<EmployeeMaster>();
        }
    }
}
