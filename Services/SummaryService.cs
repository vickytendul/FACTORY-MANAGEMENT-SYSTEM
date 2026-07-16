using FactoryManagementSystem.Entities;
using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Services
{
    public class SummaryService
    {
        private readonly FirestoreService _firestore;
        private const string SummaryDocId = "EmployeeSummary";

        public SummaryService(FirestoreService firestore)
        {
            _firestore = firestore;
        }

        private async Task<EmployeeSummary> GetOrCreateAsync()
        {
            var docRef = _firestore.Summary.Document(SummaryDocId);
            var snapshot = await docRef.GetSnapshotAsync();
            if (snapshot.Exists)
                return snapshot.ConvertTo<EmployeeSummary>();
            var empty = new EmployeeSummary();
            await docRef.SetAsync(empty);
            return empty;
        }

        public async Task OnEmployeeAdded(string? department, string? designation)
        {
            var s = await GetOrCreateAsync();
            s.TotalManpower++;
            var cat = Categorize(department, designation);
            if (cat != null) IncTotal(s, cat);
            s.TotalBalance = s.TotalManpower - s.TotalAllocated;
            await _firestore.Summary.Document(SummaryDocId).SetAsync(s);
        }

        public async Task OnEmployeeUpdated(string? oldDept, string? oldDesig, string? newDept, string? newDesig)
        {
            var oldCat = Categorize(oldDept, oldDesig);
            var newCat = Categorize(newDept, newDesig);
            if (oldCat == newCat) return;

            var s = await GetOrCreateAsync();
            if (oldCat != null) DecTotal(s, oldCat);
            if (newCat != null) IncTotal(s, newCat);
            await _firestore.Summary.Document(SummaryDocId).SetAsync(s);
        }

        public async Task OnEmployeeToggled(string? department, string? designation, bool wasActive, bool nowActive)
        {
            var s = await GetOrCreateAsync();
            var cat = Categorize(department, designation);

            if (!wasActive && nowActive)
            {
                s.TotalManpower++;
                if (cat != null) IncTotal(s, cat);
            }
            else if (wasActive && !nowActive)
            {
                s.TotalManpower--;
                if (cat != null) DecTotal(s, cat);
                if (s.TotalAllocated > 0)
                {
                    s.TotalAllocated--;
                    if (cat != null) DecAlloc(s, cat);
                }
            }

            s.TotalBalance = s.TotalManpower - s.TotalAllocated;
            await _firestore.Summary.Document(SummaryDocId).SetAsync(s);
        }

        public async Task OnEmployeeAllocated(string? department, string? designation)
        {
            var s = await GetOrCreateAsync();
            s.TotalAllocated++;
            s.TotalBalance = s.TotalManpower - s.TotalAllocated;
            var cat = Categorize(department, designation);
            if (cat != null) IncAlloc(s, cat);
            await _firestore.Summary.Document(SummaryDocId).SetAsync(s);
        }

        public async Task OnEmployeeDeallocated(string? department, string? designation)
        {
            var s = await GetOrCreateAsync();
            if (s.TotalAllocated > 0) s.TotalAllocated--;
            s.TotalBalance = s.TotalManpower - s.TotalAllocated;
            var cat = Categorize(department, designation);
            if (cat != null && s.TotalAllocated >= 0) DecAlloc(s, cat);
            await _firestore.Summary.Document(SummaryDocId).SetAsync(s);
        }

        public async Task<EmployeeMaster?> FindEmployeeByCodeAsync(string employeeCode)
        {
            var snapshot = await _firestore.EmployeeMasters
                .WhereEqualTo(nameof(EmployeeMaster.EmployeeCode), employeeCode)
                .Limit(1)
                .GetSnapshotAsync();
            return snapshot.Documents.FirstOrDefault()?.ConvertTo<EmployeeMaster>();
        }

        public async Task RecalculateAsync()
        {
            var empSnapshot = await _firestore.EmployeeMasters.GetSnapshotAsync();
            var employees = empSnapshot.Documents
                .Select(x => x.ConvertTo<EmployeeMaster>())
                .Where(x => x.IsActive)
                .ToList();

            var layoutSnapshot = await _firestore.LayoutTransactions
                .WhereEqualTo("IsActive", true)
                .GetSnapshotAsync();

            var allocatedCodes = layoutSnapshot.Documents
                .Select(x => x.ConvertTo<LayoutTransaction>())
                .Where(x => x.IsActive && !string.IsNullOrWhiteSpace(x.EmployeeCode))
                .Select(x => x.EmployeeCode.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var s = new EmployeeSummary();
            foreach (var emp in employees)
            {
                var cat = Categorize(emp.Department, emp.Designation);
                if (cat == null) continue;
                IncTotal(s, cat);
                if (allocatedCodes.Contains(emp.EmployeeCode))
                    IncAlloc(s, cat);
            }
            s.TotalManpower = employees.Count;
            s.TotalAllocated = employees.Count(e => allocatedCodes.Contains(e.EmployeeCode));
            s.TotalBalance = s.TotalManpower - s.TotalAllocated;
            await _firestore.Summary.Document(SummaryDocId).SetAsync(s);
        }

        private static void IncTotal(EmployeeSummary s, string cat)
        {
            switch (cat)
            {
                case "Tailor":           s.TailorTotal++;          break;
                case "Sewing Helper":    s.SewingHelperTotal++;    break;
                case "Sewing Leader":    s.SewingLeaderTotal++;    break;
                case "Quality Checking": s.QualityCheckingTotal++; break;
                case "Packing Helper":   s.PackingHelperTotal++;   break;
                case "Store Helper":     s.StoreHelperTotal++;     break;
            }
        }

        private static void DecTotal(EmployeeSummary s, string cat)
        {
            switch (cat)
            {
                case "Tailor":           if (s.TailorTotal > 0)          s.TailorTotal--;          break;
                case "Sewing Helper":    if (s.SewingHelperTotal > 0)    s.SewingHelperTotal--;    break;
                case "Sewing Leader":    if (s.SewingLeaderTotal > 0)    s.SewingLeaderTotal--;    break;
                case "Quality Checking": if (s.QualityCheckingTotal > 0) s.QualityCheckingTotal--; break;
                case "Packing Helper":   if (s.PackingHelperTotal > 0)   s.PackingHelperTotal--;   break;
                case "Store Helper":     if (s.StoreHelperTotal > 0)     s.StoreHelperTotal--;     break;
            }
        }

        private static void IncAlloc(EmployeeSummary s, string cat)
        {
            switch (cat)
            {
                case "Tailor":           s.TailorAllocated++;          break;
                case "Sewing Helper":    s.SewingHelperAllocated++;    break;
                case "Sewing Leader":    s.SewingLeaderAllocated++;    break;
                case "Quality Checking": s.QualityCheckingAllocated++; break;
                case "Packing Helper":   s.PackingHelperAllocated++;   break;
                case "Store Helper":     s.StoreHelperAllocated++;     break;
            }
        }

        private static void DecAlloc(EmployeeSummary s, string cat)
        {
            switch (cat)
            {
                case "Tailor":           if (s.TailorAllocated > 0)          s.TailorAllocated--;          break;
                case "Sewing Helper":    if (s.SewingHelperAllocated > 0)    s.SewingHelperAllocated--;    break;
                case "Sewing Leader":    if (s.SewingLeaderAllocated > 0)    s.SewingLeaderAllocated--;    break;
                case "Quality Checking": if (s.QualityCheckingAllocated > 0) s.QualityCheckingAllocated--; break;
                case "Packing Helper":   if (s.PackingHelperAllocated > 0)   s.PackingHelperAllocated--;   break;
                case "Store Helper":     if (s.StoreHelperAllocated > 0)     s.StoreHelperAllocated--;     break;
            }
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
