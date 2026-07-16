namespace FactoryManagementSystem.Controllers
{
    public class LayoutMasterSaveRequest
    {
        public string OperationName { get; set; } = string.Empty;
        public string Section { get; set; } = "MAIN";
        public string MachineType { get; set; } = string.Empty;
        public string OperationGrade { get; set; } = string.Empty;
    }
}
