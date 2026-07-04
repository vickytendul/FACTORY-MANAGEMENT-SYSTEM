namespace FactoryManagementSystem.Entities
{
    public class OperationMaster
    {
        public int OperationId { get; set; }

        public string OperationName { get; set; } = string.Empty;

        public string RequiredGrade { get; set; } = string.Empty;

        public decimal SMV { get; set; }

        public bool IsActive { get; set; } = true;
    }
}