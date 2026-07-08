namespace FactoryManagementSystem.Entities
{
    public class LayoutTransactionRequest
    {
        public int ZoneId { get; set; }
        public string ZoneName { get; set; } = string.Empty;

        public int LineId { get; set; }
        public string LineName { get; set; } = string.Empty;

        public int CCId { get; set; }
        public string CCNo { get; set; } = string.Empty;

        public List<LayoutTransactionItem> Items { get; set; } = new();
    }

    public class LayoutTransactionItem
    {
        // Unique Layout Row Id
        public int LayoutMasterId { get; set; }

        public int OperationId { get; set; }

        public string OperationName { get; set; } = string.Empty;

        public string OperationGrade { get; set; } = string.Empty;

        public string MachineType { get; set; } = string.Empty;

        public string Section { get; set; } = "MAIN";

        public string EmployeeCode { get; set; } = string.Empty;

        public string EmployeeBarcode { get; set; } = string.Empty;

        public string EmployeeName { get; set; } = string.Empty;

        public string EmployeeGrade { get; set; } = string.Empty;
    }
}