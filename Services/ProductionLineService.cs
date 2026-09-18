using FactoryManagementSystem.Entities;
using Microsoft.Extensions.Caching.Memory;

namespace FactoryManagementSystem.Services
{
    /// What the Company production API (SewingProdRept) says about the
    /// factory today: which lines exist, and what each of them has made.
    ///
    /// Why the line list comes from here rather than Firestore's Lines
    /// collection: that collection is this app's own master data and has to
    /// be maintained by hand, so it drifts. The production API reports the
    /// lines the floor is actually reporting against.
    ///
    /// One report answers both questions, so it is fetched once and cached
    /// - asking for the line list and asking for output does not cost two
    /// vendor round trips.
    public class ProductionLineService
    {
        private const int UnitCode = 14;

        // Output climbs through the working day, so this is short enough to
        // stay current and long enough that a burst of page loads does not
        // hammer the vendor.
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(3);

        private readonly CompanyApiClient _companyApiClient;
        private readonly IMemoryCache _cache;

        public ProductionLineService(CompanyApiClient companyApiClient, IMemoryCache cache)
        {
            _companyApiClient = companyApiClient;
            _cache = cache;
        }

        /// Line number -> what that line produced on [date]. Never throws:
        /// a vendor failure yields an empty map and callers fall back to
        /// what they already know rather than losing the page.
        public async Task<IReadOnlyDictionary<int, LineProduction>> GetProductionAsync(DateTime date)
        {
            var dateKey = CompanyApiClient.FormatDate(date.Date);
            var cacheKey = $"ProductionByLine::{dateKey}";

            if (_cache.TryGetValue(cacheKey, out IReadOnlyDictionary<int, LineProduction>? cached) &&
                cached != null)
            {
                return cached;
            }

            var byLine = new SortedDictionary<int, LineProduction>();
            try
            {
                var rows = await _companyApiClient.FetchSewingProductionReportAsync(
                    new SewingProdReptRequest
                    {
                        FDate = dateKey,
                        TDate = dateKey,
                        Line_No = 0,
                        CC_No = "-",
                        Unit_Code = UnitCode,
                    });

                foreach (var row in rows)
                {
                    // The vendor repeats every row under a "9999-01-01"
                    // sentinel date. Taking only rows stamped with the date
                    // actually asked for drops those without having to know
                    // anything about the sentinel itself.
                    if (!DateTime.TryParse(row.EffectFrom, out var effectFrom)) continue;
                    if (effectFrom.Date != date.Date) continue;

                    var isReject = string.Equals(row.Type, "REJ", StringComparison.OrdinalIgnoreCase);

                    foreach (var (key, value) in row.Operations)
                    {
                        if (!int.TryParse(key, out var lineNo) || lineNo <= 0) continue;

                        byLine.TryGetValue(lineNo, out var current);
                        byLine[lineNo] = isReject
                            ? current with { Rejects = current.Rejects + (double)value }
                            : current with { Output = current.Output + (double)value };
                    }
                }
            }
            catch
            {
                // Fall through with whatever was parsed - see method doc.
            }

            var result = (IReadOnlyDictionary<int, LineProduction>)byLine;
            _cache.Set(cacheKey, result, CacheTtl);
            return result;
        }

        /// Line numbers reported for [date], ascending.
        public async Task<List<int>> GetLineNumbersAsync(DateTime date) =>
            (await GetProductionAsync(date)).Keys.ToList();
    }

    /// One line's production for a day.
    public readonly record struct LineProduction(double Output, double Rejects);
}
