namespace FactoryManagementSystem.Entities
{
    /// Somebody put against a department work detail - the department
    /// equivalent of a layout transaction.
    ///
    /// Two partial unique indexes carry the rules that matter: one active
    /// row per work detail, and one active row per employee. Nobody stands
    /// in two places, and no job has two people.
    ///
    /// The second of those cannot reach across to the sewing layouts,
    /// which live in another table, so the controller checks that before
    /// it writes. A constraint would be better; there is not one to be had.
    public class DepartmentAllocation
    {
        public long Id { get; set; }
        public long WorkDetailId { get; set; }
        public string EmployeeCode { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
        public DateTime AllocatedOn { get; set; }
        public string AllocatedBy { get; set; } = string.Empty;
    }
}
