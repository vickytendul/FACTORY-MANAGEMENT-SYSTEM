using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Entities
{
    [FirestoreData]
    public class EmployeeSummary
    {
        [FirestoreProperty] public int TotalManpower { get; set; }
        [FirestoreProperty] public int TotalAllocated { get; set; }
        [FirestoreProperty] public int TotalBalance { get; set; }

        [FirestoreProperty] public int TailorTotal { get; set; }
        [FirestoreProperty] public int TailorAllocated { get; set; }
        [FirestoreProperty] public int TailorBalance { get; set; }

        [FirestoreProperty] public int SewingHelperTotal { get; set; }
        [FirestoreProperty] public int SewingHelperAllocated { get; set; }
        [FirestoreProperty] public int SewingHelperBalance { get; set; }

        [FirestoreProperty] public int SewingLeaderTotal { get; set; }
        [FirestoreProperty] public int SewingLeaderAllocated { get; set; }
        [FirestoreProperty] public int SewingLeaderBalance { get; set; }

        [FirestoreProperty] public int QualityCheckingTotal { get; set; }
        [FirestoreProperty] public int QualityCheckingAllocated { get; set; }
        [FirestoreProperty] public int QualityCheckingBalance { get; set; }

        [FirestoreProperty] public int PackingHelperTotal { get; set; }
        [FirestoreProperty] public int PackingHelperAllocated { get; set; }
        [FirestoreProperty] public int PackingHelperBalance { get; set; }

        [FirestoreProperty] public int StoreHelperTotal { get; set; }
        [FirestoreProperty] public int StoreHelperAllocated { get; set; }
        [FirestoreProperty] public int StoreHelperBalance { get; set; }
    }
}
