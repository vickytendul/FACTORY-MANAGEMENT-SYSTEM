namespace FactoryManagementSystem.Services
{
    /// Keeps the service from being spun down for inactivity.
    ///
    /// Render's free plan stops an instance that has had no traffic for a
    /// while, and the next request pays 50 seconds or more to start it
    /// again. That is the first page of the morning, every morning.
    ///
    /// So the service asks for its own health endpoint on a timer. The
    /// request leaves the instance, goes out to the public URL and comes
    /// back through Render's router, which is inbound traffic as far as the
    /// spin-down timer is concerned.
    ///
    /// Two things worth knowing before turning it on:
    ///
    ///   An instance that never sleeps uses its hours. Render's free plan
    ///   allows 750 instance-hours a month and a month is about 730, so one
    ///   always-on service fits - and a second one does not.
    ///
    ///   It cannot wake an instance that is already asleep. It prevents the
    ///   sleep; it does not cure it.
    ///
    /// Off unless KeepAwake:Url is set, so nothing happens by accident in
    /// development or on a plan that does not need it.
    public sealed class KeepAwakeService : BackgroundService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<KeepAwakeService> _logger;

        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        public KeepAwakeService(IConfiguration configuration, ILogger<KeepAwakeService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var url = (_configuration["KeepAwake:Url"] ?? string.Empty).Trim();
            if (url.Length == 0)
            {
                _logger.LogInformation("KeepAwake is off - KeepAwake:Url is not set.");
                return;
            }

            // Comfortably inside Render's idle window, which is fifteen
            // minutes. Short enough that one missed ping does not let the
            // instance go down.
            var minutes = int.TryParse(_configuration["KeepAwake:IntervalMinutes"], out var m) && m > 0
                ? m
                : 10;
            var interval = TimeSpan.FromMinutes(minutes);

            _logger.LogInformation(
                "KeepAwake pinging {Url} every {Minutes} minute(s).", url, minutes);

            // The first ping waits a full interval. Starting up IS traffic,
            // so pinging immediately would only tell us what we already
            // know.
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    using var response = await _httpClient.GetAsync(url, stoppingToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning(
                            "KeepAwake ping returned HTTP {Status}.", (int)response.StatusCode);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A failed ping is not worth failing the service over -
                    // the worst it costs is one cold start.
                    _logger.LogWarning("KeepAwake ping failed: {Message}", ex.Message);
                }
            }
        }
    }
}
