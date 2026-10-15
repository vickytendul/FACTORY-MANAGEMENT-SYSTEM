using System.Globalization;
using System.Collections.Concurrent;
using FactoryManagementSystem.Entities;

namespace FactoryManagementSystem.Services.Users
{
    /// Opening an account from the Company API, so eight hundred people do
    /// not have to be entered by hand.
    ///
    /// Payroll knows who works here; it does not know a password, and
    /// nothing it returns is one. So it is used for the half it can
    /// answer - is this a real, current employee, and is the person at
    /// the screen them - and the password is ours from then on.
    ///
    /// The proof is a date of birth or the last four digits of a mobile
    /// number. Neither is a secret in any serious sense: a colleague may
    /// well know both. What makes this safe enough is that it happens
    /// ONCE. After it, the account has a PIN nobody else chose, and
    /// somebody enrolling in your name is not quiet - your own enrolment
    /// then fails, and you find out the first time you try.
    ///
    /// It is not bank-grade and is not meant to be. It is a door that
    /// opens for people who work here and stays shut for people who do
    /// not.
    public sealed class EnrolmentService
    {
        private readonly CompanyApiClient _companyApi;
        private readonly IUserRepository _users;
        private readonly IConfiguration _config;
        private readonly ILogger<EnrolmentService> _logger;

        public EnrolmentService(
            CompanyApiClient companyApi,
            IUserRepository users,
            IConfiguration config,
            ILogger<EnrolmentService> logger)
        {
            _companyApi = companyApi;
            _users = users;
            _config = config;
            _logger = logger;
        }

        private const int CompCode = 17;

        /// What a new account gets until an Admin says otherwise.
        ///
        /// Supervisor, because an account that can see nothing is an
        /// account somebody has to go and ask about - which is the manual
        /// step this exists to remove. Set Enrolment__DefaultRole to
        /// change it without a deploy.
        private string DefaultRole =>
            (_config["Enrolment:DefaultRole"] ?? "Supervisor").Trim();

        /// Tries per employee code, so a date of birth cannot be worked
        /// through four digits at a time.
        ///
        /// In memory, which means it resets when the instance restarts
        /// and is not shared if this is ever run on two. Said plainly
        /// rather than dressed up: it raises the cost of guessing, it
        /// does not make guessing impossible. The attack it does not
        /// stop - a colleague who simply knows your birthday - is the
        /// likelier one, and no counter helps with that. What helps with
        /// that is that enrolling as somebody else is loud.
        private static readonly ConcurrentDictionary<string, (int Tries, DateTime Since)>
            _attempts = new(StringComparer.OrdinalIgnoreCase);

        private const int MaxTries = 5;
        private static readonly TimeSpan Window = TimeSpan.FromHours(1);

        public sealed record Result(bool Ok, string Message, AppUser? User = null);

        public async Task<Result> EnrolAsync(string employeeCode, string proof, string pin)
        {
            var code = (employeeCode ?? string.Empty).Trim().ToUpperInvariant();
            if (code.Length == 0)
                return new Result(false, "Enter your employee code.");

            if (!IsPinAcceptable(pin))
            {
                return new Result(false,
                    "Choose a PIN of 4 to 6 digits that is not all the same digit.");
            }

            // Already enrolled is not a failure to explain in detail: an
            // account exists, so the answer is to log in or to ask for a
            // reset, and saying which accounts exist is not this
            // endpoint's business.
            if (await _users.FindByUsernameAsync(code) != null)
            {
                return new Result(false,
                    "This employee code already has an account. Log in, "
                    + "or ask an Admin to reset the PIN.");
            }

            if (TooManyTries(code))
            {
                return new Result(false,
                    "Too many attempts. Try again in an hour, or ask an Admin.");
            }

            List<CompanyApiEmployee> roster;
            try
            {
                var today = DateTime.Now.Date;
                roster = await _companyApi.FetchEmployeesAsync(CompCode, today, today);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Enrolment could not read the payroll roster");
                return new Result(false,
                    "Payroll could not be reached just now. Try again shortly.");
            }

            var person = roster.FirstOrDefault(e =>
                string.Equals((e.Tno ?? string.Empty).Trim(), code,
                    StringComparison.OrdinalIgnoreCase));

            // A code that is not on today's roster, and a wrong proof for
            // one that is, get the same answer. A different one would
            // turn this into a way of asking who works here.
            if (person == null || !person.IsStillEmployedOn(DateTime.Now.Date)
                || !ProofMatches(person, proof))
            {
                RecordTry(code);
                return new Result(false,
                    "That employee code and detail do not match our records.");
            }

            var user = new AppUser
            {
                Username = code,
                DisplayName = (person.Name ?? code).Trim(),
                // Work factor 12, the same target a login rehashes to, so
                // an enrolled account is not slower to sign in than an
                // Admin-created one.
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(pin, 12),
                Role = DefaultRole,
                IsActive = true,
                CreatedOn = DateTime.UtcNow,
            };

            await _users.CreateAsync(user);
            _attempts.TryRemove(code, out _);
            _logger.LogInformation(
                "Enrolled {Code} as {Role} from the payroll roster", code, DefaultRole);

            return new Result(true, "Account created.", user);
        }

        /// A date of birth, or the last four of a mobile number.
        ///
        /// Both come from the same roster row, and either will do: some
        /// people know their number by heart and not their recorded date
        /// of birth, and two of the eight hundred have no usable number
        /// at all.
        private static bool ProofMatches(CompanyApiEmployee person, string? proof)
        {
            var given = new string((proof ?? string.Empty).Where(char.IsDigit).ToArray());
            if (given.Length == 0) return false;

            // The barcode is a date of birth, a six-digit serial and a
            // letter - measured across the whole roster, eight hundred
            // and ten of eight hundred and ten parse as a real date
            // between 1969 and 2008. The date is the only part used.
            var barcode = (person.Barcode ?? string.Empty).Trim();
            if (barcode.Length >= 8)
            {
                var head = barcode[..8];
                if (DateTime.TryParseExact(head, "ddMMyyyy",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    // Typed as 01011990, 01-01-1990 or 1/1/1990 - the
                    // digits are compared, not the punctuation somebody
                    // happened to use.
                    if (FixedTimeEquals(given, head)) return true;
                }
            }

            var contact = new string(
                (person.Contact ?? string.Empty).Where(char.IsDigit).ToArray());
            if (contact.Length >= 4 && given.Length == 4)
            {
                if (FixedTimeEquals(given, contact[^4..])) return true;
            }

            return false;
        }

        /// Four to six digits, and not 0000 or 111111.
        ///
        /// Not a password policy - this is a factory floor and a PIN is
        /// typed standing up - but a PIN of one repeated digit is the
        /// first thing anybody tries, and letting somebody choose it is
        /// handing their account away.
        private static bool IsPinAcceptable(string? pin)
        {
            var p = (pin ?? string.Empty).Trim();
            if (p.Length is < 4 or > 6) return false;
            if (!p.All(char.IsDigit)) return false;
            return p.Distinct().Count() > 1;
        }

        private static bool TooManyTries(string code)
        {
            if (!_attempts.TryGetValue(code, out var state)) return false;
            if (DateTime.UtcNow - state.Since > Window)
            {
                _attempts.TryRemove(code, out _);
                return false;
            }
            return state.Tries >= MaxTries;
        }

        private static void RecordTry(string code) =>
            _attempts.AddOrUpdate(
                code,
                _ => (1, DateTime.UtcNow),
                (_, state) => DateTime.UtcNow - state.Since > Window
                    ? (1, DateTime.UtcNow)
                    : (state.Tries + 1, state.Since));

        /// Compared in fixed time: four digits and eight digits are both
        /// small enough spaces that answering a near miss faster than a
        /// far one is worth closing off.
        private static bool FixedTimeEquals(string a, string b)
        {
            var left = System.Text.Encoding.UTF8.GetBytes(a);
            var right = System.Text.Encoding.UTF8.GetBytes(b);
            return left.Length == right.Length
                && System.Security.Cryptography.CryptographicOperations
                    .FixedTimeEquals(left, right);
        }
    }
}
