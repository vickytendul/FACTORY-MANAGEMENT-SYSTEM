using Google.Cloud.Firestore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FactoryManagementSystem.Entities
{
    [Table("LayoutTransaction")]
    [FirestoreData]
    public class LayoutTransaction
    {
        [Key]
        [FirestoreProperty]
        public int TransactionId { get; set; }

        // Firestore Document Id (for Update/Delete)
        [FirestoreDocumentId]
        public string FirestoreId { get; set; } = string.Empty;

        // Layout Row Reference
        [FirestoreProperty]
        public int LayoutMasterId { get; set; }

        // Production Details
        [FirestoreProperty]
        public int ZoneId { get; set; }

        [FirestoreProperty]
        public string ZoneName { get; set; } = string.Empty;

        [FirestoreProperty]
        public int LineId { get; set; }

        [FirestoreProperty]
        public string LineName { get; set; } = string.Empty;

        [FirestoreProperty]
        public int CCId { get; set; }

        [FirestoreProperty]
        public string CCNo { get; set; } = string.Empty;

        // Operation Details
        [FirestoreProperty]
        public int OperationId { get; set; }

        [FirestoreProperty]
        public string OperationName { get; set; } = string.Empty;

        [FirestoreProperty]
        public string OperationGrade { get; set; } = string.Empty;

        [FirestoreProperty]
        public string MachineType { get; set; } = string.Empty;

        [FirestoreProperty]
        public string Section { get; set; } = "MAIN";

        // Employee Details
        [FirestoreProperty]
        public string EmployeeCode { get; set; } = string.Empty;

        [FirestoreProperty]
        public string EmployeeBarcode { get; set; } = string.Empty;

        [FirestoreProperty]
        public string EmployeeName { get; set; } = string.Empty;

        [FirestoreProperty]
        public string EmployeeGrade { get; set; } = string.Empty;

        // Audit
        [FirestoreProperty]
        public DateTime AllocationDate { get; set; }

        [FirestoreProperty]
        public DateTime AllocatedDateTime { get; set; }

        [FirestoreProperty]
        public string? AllocatedBy { get; set; }

        [FirestoreProperty]
        public bool IsActive { get; set; } = true;
    }
}