using System.ComponentModel.DataAnnotations;

namespace FactoryManagementSystem.Entities
{
    public class CCLayout
    {
        [Key]
        public int LayoutId { get; set; }

        public int CCId { get; set; }

        public CC? CC { get; set; }

        public int OperationId { get; set; }

        public OperationMaster? OperationMaster { get; set; }

        public int OperationSequence { get; set; }

        public bool IsActive { get; set; } = true;
    }
}