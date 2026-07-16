using FactoryManagementSystem.Entities;
using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Services
{
    public class SummaryService
    {
        private readonly FirestoreService _firestore;

        public SummaryService(FirestoreService firestore)
        {
            _firestore = firestore;
        }

        public async Task RecalculateAsync()
        {
            var empSnapshot = await _firestore.EmployeeMasters.GetSnapshotAsync();
            var employees = empSnapshot.Documents
                .Select(x => x.ConvertTo<EmployeeMaster>())
                .ToList();

            var layoutSnapshot = await _firestore.LayoutTransactions
                .WhereEqualTo("IsActive", true)
                .GetSnapshotAsync();

            var allocatedCodes = layoutSnapshot.Documents
     .Select(x => x.ConvertTo<LayoutTransaction>())
     .Where(x => x.IsActive && !string.IsNullOrWhiteSpace(x.EmployeeCode))
     .Select(x => x.EmployeeCode.Trim())
     .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var catTailor = 0; var catTailorAlloc = 0;
            var catSewingHelper = 0; var catSewingHelperAlloc = 0;
            var catSewingLeader = 0; var catSewingLeaderAlloc = 0;
            var catQuality = 0; var catQualityAlloc = 0;
            var catPacking = 0; var catPackingAlloc = 0;
            var catStore = 0; var catStoreAlloc = 0;

            foreach (var emp in employees)
            {
                var cat = Categorize(emp.Department, emp.Designation);
                if (cat == null) continue;

                var isAlloc = allocatedCodes.Contains(emp.EmployeeCode);

                switch (cat)
                {
                    case "Tailor":            catTailor++;       if (isAlloc) catTailorAlloc++;       break;
                    case "Sewing Helper":     catSewingHelper++;  if (isAlloc) catSewingHelperAlloc++;  break;
                    case "Sewing Leader":     catSewingLeader++;  if (isAlloc) catSewingLeaderAlloc++;  break;
                    case "Quality Checking":  catQuality++;       if (isAlloc) catQualityAlloc++;       break;
                    case "Packing Helper":    catPacking++;       if (isAlloc) catPackingAlloc++;       break;
                    case "Store Helper":      catStore++;         if (isAlloc) catStoreAlloc++;         break;
                }
            }

            var totalCount = employees.Count;
            var totalAllocated = employees.Count(e => allocatedCodes.Contains(e.EmployeeCode));

            var summary = new EmployeeSummary
            {
                TotalManpower = totalCount,
                TotalAllocated = totalAllocated,
                TotalBalance = totalCount - totalAllocated,

                TailorTotal = catTailor,
                TailorAllocated = catTailorAlloc,
                TailorBalance = catTailor - catTailorAlloc,

                SewingHelperTotal = catSewingHelper,
                SewingHelperAllocated = catSewingHelperAlloc,
                SewingHelperBalance = catSewingHelper - catSewingHelperAlloc,

                SewingLeaderTotal = catSewingLeader,
                SewingLeaderAllocated = catSewingLeaderAlloc,
                SewingLeaderBalance = catSewingLeader - catSewingLeaderAlloc,

                QualityCheckingTotal = catQuality,
                QualityCheckingAllocated = catQualityAlloc,
                QualityCheckingBalance = catQuality - catQualityAlloc,

                PackingHelperTotal = catPacking,
                PackingHelperAllocated = catPackingAlloc,
                PackingHelperBalance = catPacking - catPackingAlloc,

                StoreHelperTotal = catStore,
                StoreHelperAllocated = catStoreAlloc,
                StoreHelperBalance = catStore - catStoreAlloc,
            };

            await _firestore.Summary.Document("EmployeeSummary").SetAsync(summary);
        }

        private static string? Categorize(string? department, string? designation)
        {
            var dept = (department ?? "").Trim().ToUpperInvariant();
            var desig = (designation ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(dept)) return null;
            if (dept.StartsWith("TAILOR") || desig.StartsWith("TAILOR")) return "Tailor";
            if (dept == "QUALITY") return "Quality Checking";
            if (dept == "PACKING" && desig.Contains("HELPER")) return "Packing Helper";
            if (dept == "STORE" && desig.Contains("HELPER")) return "Store Helper";
            if (dept == "SEWING" && desig.Contains("HELPER")) return "Sewing Helper";
            if (dept == "SEWING" && (desig.Contains("LEADER") || desig.Contains("LEADEAR"))) return "Sewing Leader";
            return null;
        }
    }
}
