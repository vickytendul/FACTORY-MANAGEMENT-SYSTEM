using System.Text.Json;

namespace FactoryManagementSystem.Services.Attendance
{
    /// Takes today's payroll attendance and keeps it, on a timer.
    ///
    /// The report keeps whatever it is handed while somebody is looking at
    /// it, which covers every day anybody opens the app. This covers the
    /// days nobody does - a Sunday, a holiday, the day the office is shut -
    /// because the vendor only answers for a range that includes today,
    /// and a day that goes uncaptured can never be captured afterwards.
    ///
    /// It runs late and often rather than once: payroll posts through the
    /// day, and the last answer before midnight is the one worth having.
    /// Writing over the same day costs one statement.
    public sealed class PayrollCaptureService : BackgroundService
    {
        private const int CompCode = 17;

        private readonly IServiceProvider _services;
        private readonly IConfiguration _configuration;
        private readonly ILogger<PayrollCaptureService> _logger;

        public PayrollCaptureService(
            IServiceProvider services,
            IConfiguration configuration,
            ILogger<PayrollCaptureService> logger)
        {
            _services = services;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var minutes = int.TryParse(
                _configuration["PayrollCapture:IntervalMinutes"], out var m) && m > 0
                ? m
                : 60;
            var interval = TimeSpan.FromMinutes(minutes);

            _logger.LogInformation(
                "Payroll capture every {Minutes} minute(s).", minutes);

            // A moment after start, so it does not compete with the rest of
            // the application coming up.
            try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CaptureTodayAsync();
                }
                catch (Exception ex)
                {
                    // Nothing downstream is waiting on this. A failure
                    // costs tomorrow's reading of today, and the next run
                    // an hour from now repairs it.
                    _logger.LogError(ex, "Payroll capture failed.");
                }

                try { await Task.Delay(interval, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task CaptureTodayAsync()
        {
            using var scope = _services.CreateScope();
            var snapshots = scope.ServiceProvider
                .GetService<PayrollSnapshotRepository>();
            if (snapshots == null) return;

            var client = scope.ServiceProvider.GetRequiredService<CompanyApiClient>();

            var today = DateTime.Now.Date;
            var key = CompanyApiClient.FormatDate(today);

            var (success, statusCode, body) = await client.FetchRawAsync(
                CompCode, key, key);

            if (!success)
            {
                _logger.LogWarning(
                    "Payroll capture - Employee_Att returned HTTP {Status}.", statusCode);
                return;
            }
            if (string.IsNullOrWhiteSpace(body)) return;

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            var statuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var employee in doc.RootElement.EnumerateArray())
            {
                if (!employee.TryGetProperty("tno", out var tnoProp)) continue;
                var tno = tnoProp.ValueKind == JsonValueKind.String
                    ? tnoProp.GetString()
                    : tnoProp.ToString();
                if (string.IsNullOrWhiteSpace(tno)) continue;

                if (employee.TryGetProperty(key, out var statusProp))
                {
                    statuses[tno.Trim()] = statusProp.ValueKind == JsonValueKind.String
                        ? statusProp.GetString() ?? string.Empty
                        : statusProp.ToString();
                }
            }

            if (statuses.Count == 0)
            {
                _logger.LogInformation(
                    "Payroll capture - nothing posted for {Date} yet.", key);
                return;
            }

            await snapshots.SaveAsync(today, statuses);
            _logger.LogInformation(
                "Payroll capture - kept {Count} statuses for {Date}.",
                statuses.Count, key);
        }
    }
}
