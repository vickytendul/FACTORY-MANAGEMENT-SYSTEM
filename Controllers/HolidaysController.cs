using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace FactoryManagementSystem.Controllers
{
    /// The days the factory does not run.
    ///
    /// Sundays are known without being told. A declared holiday is not:
    /// on Gandhi Jayanti payroll marked 822 people AB and 27 P - the
    /// twenty-seven being security and maintenance - and nothing in the
    /// response distinguishes that from a day everybody happened to stay
    /// home. So it is recorded rather than guessed at.
    ///
    /// A date here is left out of the comparison columns and out of every
    /// average, exactly as a Sunday is. Nothing is deleted: ask for the
    /// date directly and it still answers.
    [ApiController]
    [Route("api/[controller]")]
    public class HolidaysController : ControllerBase
    {
        private readonly NpgsqlDataSource? _dataSource;
        private readonly ILogger<HolidaysController> _log;

        public HolidaysController(
            ILogger<HolidaysController> log,
            NpgsqlDataSource? dataSource = null)
        {
            _log = log;
            _dataSource = dataSource;
        }

        /// Every holiday in a year, or in all of them when no year is
        /// given. The reports ask for the year they are showing.
        [HttpGet]
        public async Task<IActionResult> Get([FromQuery] int? year)
        {
            if (_dataSource == null) return Ok(Array.Empty<object>());

            try
            {
                await using var cmd = _dataSource.CreateCommand(
                    year == null
                        ? "select holiday_date, name from public.factory_holidays "
                          + "order by holiday_date"
                        : "select holiday_date, name from public.factory_holidays "
                          + "where extract(year from holiday_date) = @y "
                          + "order by holiday_date");
                if (year != null) cmd.Parameters.AddWithValue("y", year.Value);

                await using var r = await cmd.ExecuteReaderAsync();
                var rows = new List<object>();
                while (await r.ReadAsync())
                {
                    rows.Add(new
                    {
                        date = r.GetDateTime(0).ToString("yyyy-MM-dd"),
                        name = r.GetString(1),
                    });
                }
                return Ok(rows);
            }
            catch (Exception ex)
            {
                // A report that cannot read the holidays shows a holiday
                // column rather than no report at all, so this answers
                // empty instead of failing.
                _log.LogError(ex, "Holidays could not be read");
                return Ok(Array.Empty<object>());
            }
        }

        [HttpPost]
        public async Task<IActionResult> Add([FromBody] HolidayRequest request)
        {
            if (_dataSource == null) return SupabaseMissing();
            if (!DateTime.TryParse(request.Date, out var date))
                return BadRequest(new { Success = false, Message = "A date is required." });

            await using var cmd = _dataSource.CreateCommand("""
                insert into public.factory_holidays (holiday_date, name)
                values (@d, @n)
                on conflict (holiday_date) do update set name = excluded.name
                """);
            cmd.Parameters.AddWithValue("d", date.Date);
            cmd.Parameters.AddWithValue("n", (request.Name ?? string.Empty).Trim());
            await cmd.ExecuteNonQueryAsync();

            return Ok(new
            {
                Success = true,
                Date = date.ToString("yyyy-MM-dd"),
                Message = "Recorded. It is out of the columns and the averages from now on.",
            });
        }

        [HttpDelete("{date}")]
        public async Task<IActionResult> Remove(string date)
        {
            if (_dataSource == null) return SupabaseMissing();
            if (!DateTime.TryParse(date, out var d))
                return BadRequest(new { Success = false, Message = "A date is required." });

            await using var cmd = _dataSource.CreateCommand(
                "delete from public.factory_holidays where holiday_date = @d");
            cmd.Parameters.AddWithValue("d", d.Date);
            var removed = await cmd.ExecuteNonQueryAsync();

            return Ok(new
            {
                Success = true,
                Removed = removed,
                Message = removed == 0
                    ? "Nothing was recorded for that date."
                    : "Removed. It counts as a working day again.",
            });
        }

        public class HolidayRequest
        {
            public string? Date { get; set; }
            public string? Name { get; set; }
        }

        private IActionResult SupabaseMissing() => BadRequest(new
        {
            Success = false,
            Message = "Supabase is not configured on this deployment.",
        });
    }
}
