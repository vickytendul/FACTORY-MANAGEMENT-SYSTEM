using FactoryManagementSystem.Entities;

namespace FactoryManagementSystem.Services.Employees
{
    /// The employee master: the roster plus the things the Company API
    /// does not carry.
    ///
    /// Grade is why this store exists at all. The Company API gives the
    /// roster - code, name, department, designation, barcode - but it has
    /// no grade, and grade is what Layout Allocation matches an operator
    /// against an operation with. So the roster can be re-fetched from the
    /// vendor at any time and the grade cannot: it is entered here and
    /// lives only here.
    ///
    /// Two methods cover every read the app makes. The controllers filter,
    /// page and search over the whole roster in memory rather than issuing
    /// a query per keystroke, so one list is all they need.
    public interface IEmployeeRepository
    {
        /// The whole employee master. Cached by the implementation, because
        /// Skill Update's search calls through to this on every debounced
        /// keystroke.
        Task<List<EmployeeMaster>> GetAllAsync();

        /// One employee by code, or null.
        Task<EmployeeMaster?> FindByCodeAsync(string code);
    }
}
