namespace FactoryManagementSystem.Entities
{
    public class UpdateLayoutTransactionRequest
    {
        public string FirestoreId { get; set; } = string.Empty;

        public string EmployeeCode { get; set; } = string.Empty;

        public string EmployeeBarcode { get; set; } = string.Empty;

        public string EmployeeName { get; set; } = string.Empty;

        public string EmployeeGrade { get; set; } = string.Empty;
    }
}