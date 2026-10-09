namespace FactoryManagementSystem.Controllers
{
public class LayoutMasterSaveRequest
{
    public int OperationId { get; set; }
    public string OperationName { get; set; } = string.Empty;
    public string Section { get; set; } = "MAIN";
    public string MachineType { get; set; } = string.Empty;
    public string OperationGrade { get; set; } = string.Empty;

    /// False for an operation the floor is not running, so the line can
    /// read fully allocated without anybody standing on it. Defaults to
    /// true, so an older client that does not send the field leaves every
    /// row required exactly as before.
    public bool IsRequired { get; set; } = true;
}
}
