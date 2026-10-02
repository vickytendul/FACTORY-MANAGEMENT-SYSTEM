namespace FactoryManagementSystem.Entities
{
    /// One job in a department's layout - the department equivalent of an
    /// operation on a sewing line.
    ///
    /// One work detail is one person, the way one station is one operator.
    /// A department with ten people has ten of these.
    public class DepartmentWorkDetail
    {
        public long Id { get; set; }

        /// As payroll spells it, because that is where the list comes from.
        public string Department { get; set; } = string.Empty;

        /// Room for a department to have more than one layout later - a
        /// shift, say - without a migration. Only 1 is used today.
        public int LayoutNo { get; set; } = 1;

        /// Display order, deliberately NOT unique. Making it unique would
        /// mean every reorder collides with itself and has to be done in
        /// two passes through negative numbers, which is the dance the
        /// sewing layout repository has to do. An order does not need it.
        public int SNo { get; set; }

        public string WorkDetail { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
    }
}
