using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DuplicateAnalysisController : ControllerBase
{
    private readonly FirestoreDb _firestore;

    public DuplicateAnalysisController(FirestoreDb firestore)
    {
        _firestore = firestore;
    }

    [HttpGet("layout-transactions")]
    public async Task<IActionResult> AnalyzeLayoutTransactions()
    {
        var snapshot = await _firestore
            .Collection("LayoutTransactions")
            .GetSnapshotAsync();

        var totalScanned = snapshot.Documents.Count;

        // Group by composite key: EmployeeCode + LineId + CCId + Section
        var groups = snapshot.Documents
            .GroupBy(d =>
            {
                var ec = d.GetValue<string>("EmployeeCode") ?? "";
                var li = d.GetValue<int>("LineId");
                var ci = d.GetValue<int>("CCId");
                var se = (d.GetValue<string>("Section") ?? "").Trim().ToUpperInvariant();
                return (EmployeeCode: ec, LineId: li, CCId: ci, Section: se);
            })
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Count())
            .ToList();

        var duplicateGroups = new List<object>();
        var totalDuplicates = 0;
        var totalKeep = 0;
        var totalDeleteCandidates = 0;

        foreach (var group in groups)
        {
            var key = group.Key;
            var docs = group.OrderByDescending(d =>
                d.GetValue<DateTime>("AllocatedDateTime")
            ).ToList();

            var newest = docs.First();
            var candidates = docs.Skip(1).ToList();

            totalDuplicates += docs.Count;
            totalKeep++;
            totalDeleteCandidates += candidates.Count;

            duplicateGroups.Add(new
            {
                compositeKey = new
                {
                    employeeCode = key.EmployeeCode,
                    lineId = key.LineId,
                    ccId = key.CCId,
                    section = key.Section
                },
                employeeName = newest.GetValue<string>("EmployeeName") ?? "",
                lineName = newest.GetValue<string>("LineName") ?? "",
                ccNo = newest.GetValue<string>("CCNo") ?? "",
                duplicateCount = docs.Count,
                keep = DocumentToReport(newest),
                deleteCandidates = candidates.Select(DocumentToReport).ToList()
            });
        }

        var report = new
        {
            summary = new
            {
                totalDocumentsScanned = totalScanned,
                totalDuplicateGroups = duplicateGroups.Count,
                totalDuplicateDocuments = totalDuplicates,
                documentsToKeep = totalKeep,
                documentsToDelete = totalDeleteCandidates
            },
            duplicateGroups
        };

        return Ok(report);
    }

    [HttpPost("cleanup-layout-transactions")]
    public async Task<IActionResult> CleanupLayoutTransactions()
    {
        var snapshot = await _firestore
            .Collection("LayoutTransactions")
            .GetSnapshotAsync();

        var totalScanned = snapshot.Documents.Count;

        // Group by composite key: EmployeeCode + LineId + CCId + Section + OperationName
        var groups = snapshot.Documents
            .GroupBy(d =>
            {
                var ec = d.GetValue<string>("EmployeeCode") ?? "";
                var li = d.GetValue<int>("LineId");
                var ci = d.GetValue<int>("CCId");
                var se = (d.GetValue<string>("Section") ?? "").Trim().ToUpperInvariant();
                var op = (d.GetValue<string>("OperationName") ?? "").Trim();
                return (EmployeeCode: ec, LineId: li, CCId: ci, Section: se, OperationName: op);
            })
            .Where(g => g.Count() > 1)
            .ToList();

        var duplicateGroupCount = 0;
        var totalKept = 0;
        var totalDeleted = 0;
        var deletedDocs = new List<object>();

        var batch = _firestore.StartBatch();
        var batchOpCount = 0;

        foreach (var group in groups)
        {
            var docs = group.OrderByDescending(d =>
                d.GetValue<DateTime>("AllocatedDateTime")
            ).ToList();

            var newest = docs.First();
            var candidates = docs.Skip(1).ToList();

            duplicateGroupCount++;
            totalKept++;
            totalDeleted += candidates.Count;

            foreach (var doc in candidates)
            {
                batch.Delete(doc.Reference);
                deletedDocs.Add(DocumentToReport(doc));
                batchOpCount++;

                if (batchOpCount >= 400)
                {
                    await batch.CommitAsync();
                    batch = _firestore.StartBatch();
                    batchOpCount = 0;
                }
            }
        }

        if (batchOpCount > 0)
        {
            await batch.CommitAsync();
        }

        return Ok(new
        {
            totalDocumentsScanned = totalScanned,
            duplicateGroups = duplicateGroupCount,
            documentsKept = totalKept,
            documentsDeleted = totalDeleted,
            deletedDocuments = deletedDocs
        });
    }

    private static object DocumentToReport(DocumentSnapshot d)
    {
        return new
        {
            firestoreDocumentId = d.Id,
            transactionId = d.GetValue<int>("TransactionId"),
            employeeCode = d.GetValue<string>("EmployeeCode") ?? "",
            employeeName = d.GetValue<string>("EmployeeName") ?? "",
            lineId = d.GetValue<int>("LineId"),
            lineName = d.GetValue<string>("LineName") ?? "",
            ccId = d.GetValue<int>("CCId"),
            ccNo = d.GetValue<string>("CCNo") ?? "",
            section = d.GetValue<string>("Section") ?? "",
            operationName = d.GetValue<string>("OperationName") ?? "",
            machineType = d.GetValue<string>("MachineType") ?? "",
            operationGrade = d.GetValue<string>("OperationGrade") ?? "",
            allocatedDateTime = d.GetValue<DateTime>("AllocatedDateTime"),
            allocationDate = d.GetValue<DateTime>("AllocationDate"),
            isActive = d.GetValue<bool>("IsActive")
        };
    }
}
