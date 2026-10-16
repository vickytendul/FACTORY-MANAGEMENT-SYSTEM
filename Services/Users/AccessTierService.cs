using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services.Departments;

namespace FactoryManagementSystem.Services.Users
{
    /// What somebody may see, worked out from where they actually work.
    ///
    /// Roles used to be typed in one at a time and then went stale the
    /// moment anybody moved. This reads the floor instead: the placement
    /// the GM confirmed for all 810 people, falling back to payroll's own
    /// department and designation.
    ///
    /// The placement is used in preference, and it has to be. Payroll
    /// files twenty cutting workers under something else - thirteen of
    /// them as TAILOR, three as SEWING/HELPER, two as STORES/HELPER - and
    /// calls exactly one of them CUTTING. Granting access on payroll's
    /// department would have given the cutting screen to one of the
    /// twenty people who need it.
    ///
    /// Recomputed at every login, so a move on the floor changes what
    /// somebody sees the next morning with nobody editing anything.
    public sealed class AccessTierService
    {
        private readonly CompanyApiClient _companyApi;

        /// Where the floor says somebody works, from the department work
        /// details they are allocated to. Null on a deployment with no
        /// Supabase, where payroll's own department has to answer alone.
        private readonly DepartmentLayoutRepository? _departments;

        private readonly ILogger<AccessTierService> _logger;

        public AccessTierService(
            CompanyApiClient companyApi,
            ILogger<AccessTierService> logger,
            DepartmentLayoutRepository? departments = null)
        {
            _companyApi = companyApi;
            _departments = departments;
            _logger = logger;
        }

        private const int CompCode = 17;

        // The tiers, which are also what lands in app_users.role and in
        // the token the app reads its menu from.
        public const string Admin = "Admin";
        public const string Manager = "Manager";
        public const string Production = "Production";
        public const string Cutting = "Cutting";
        public const string Store = "Store";
        public const string IE = "IE";
        public const string Hr = "HR";
        public const string Quality = "Quality";
        public const string None = "None";

        /// The tier for one employee code, or null when the floor and
        /// payroll between them cannot say - which the caller treats as
        /// "leave whatever role is already stored alone" rather than as
        /// a demotion.
        public async Task<string?> TierForAsync(string employeeCode)
        {
            var code = (employeeCode ?? string.Empty).Trim();
            if (code.Length == 0) return null;

            CompanyApiEmployee? person;
            try
            {
                var today = DateTime.Now.Date;
                var roster = await _companyApi.FetchEmployeesAsync(CompCode, today, today);
                person = roster.FirstOrDefault(e =>
                    string.Equals((e.Tno ?? string.Empty).Trim(), code,
                        StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                // Payroll being unreachable must not lock anybody out of
                // a system they could use a minute ago.
                _logger.LogWarning(ex, "Access tier could not read the payroll roster");
                return null;
            }

            if (person == null) return null;

            string? section = null;
            if (_departments != null)
            {
                try
                {
                    var all = await _departments.GetPlacementsByCodeAsync();
                    if (all.TryGetValue(code, out var placed))
                    {
                        // The floor's own answer. Absent for the 650
                        // standing on a line - they are placed by layout
                        // rather than by work detail - and payroll's
                        // designation answers for them instead.
                        section = placed.Department;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Access tier could not read work details");
                }
            }

            return Tier(section, person.DeptName, person.DesignationName);
        }

        /// The rule itself, pure and in one place so it can be read
        /// without running anything.
        ///
        /// Order matters: the first match wins, and the exclusions come
        /// first. A line checker is QUALITY by department and has no
        /// business in the reports, so they are ruled out before the
        /// department is ever looked at.
        public static string Tier(
            string? workSection, string? department, string? designation)
        {
            var section = Section(workSection);
            var dept = (department ?? string.Empty).Trim().ToUpperInvariant();
            var desig = (designation ?? string.Empty).Trim().ToUpperInvariant();

            // Checkers, line QC and helpers stand on the floor doing the
            // work. Nothing in this application is for them, and giving
            // them the reports would put six hundred people in front of
            // figures nobody asked them to read.
            if (desig is "CHECKER" or "LINE QC" or "HELPER") return None;

            // The three who can open and close other people's accounts.
            if (desig is "GM-OPERATIONS" or "FACTORY MANAGER" or "AGM") return Admin;

            // HR manage people, not production. Strength Summary is a
            // headcount, which is theirs; the rest is not.
            //
            // By designation and not by department: the HR department
            // also holds a nurse, a counsellor, recruitment officers and
            // training instructors, none of whom asked for a headcount.
            // Measured across the roster, exactly three designations
            // carry HR - MANAGER - HR twice and EXECUTIVE - HR once -
            // and nothing else matches by accident.
            //
            // Before the manager test below, so MANAGER - HR lands here
            // and not in the tier that sees production.
            if (desig.Contains("HR")) return Hr;

            if (desig.Contains("MANAGER")) return Manager;

            if (section == "IED" || dept == "IED") return IE;
            if (section == "CUTTING") return Cutting;
            if (section == "STORES" || dept == "STORES") return Store;
            if (section == "QUALITY" || dept == "QUALITY") return Quality;
            if (section == "ADMIN" || dept == "ADMIN") return Manager;

            if (section == "SEWING"
                || desig is "LINE LEADER" or "SUPERVISOR" or "RECORDER"
                || desig.Contains("INCHARGE"))
            {
                return Production;
            }

            return None;
        }

        /// The part of a work detail before the dash - "CUTTING - HT
        /// STICKER" is the cutting section. Measured across the roster:
        /// the prefixes are clean, whatever the typing after them.
        private static string Section(string? workDetail)
        {
            var w = (workDetail ?? string.Empty).Trim();
            if (w.Length == 0) return string.Empty;
            var dash = w.IndexOf('-');
            return (dash < 0 ? w : w[..dash]).Trim().ToUpperInvariant();
        }
    }
}
