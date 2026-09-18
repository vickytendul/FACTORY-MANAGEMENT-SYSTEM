using FactoryManagementSystem.Entities;
using Microsoft.Extensions.Caching.Memory;

namespace FactoryManagementSystem.Services
{
    /// Which production lines the factory actually runs, according to the
    /// Company production API (SewingProdRept).
    ///
    /// Why not Firestore's Lines collection: that collection is this app's
    /// own master data and has to be maintained by hand, so it drifts. The
    /// production API reports the lines the floor is actually reporting
    /// against, which is what the Home Screen's "Total Lines" should mean.
    ///
    /// The report is requested for all lines at once (Line_No = 0), and the
    /// line numbers are the numeric keys of each row - the same reading the
    /// Layout Allocation page's own line dropdown already does.
    public class ProductionLineService
    {
        private const int UnitCode = 14;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

        private readonly CompanyApiClient _companyApiClient;
        private readonly IMemoryCache _cache;

        public ProductionLineService(CompanyApiClient companyApiClient, IMemoryCache cache)
        {
            _companyApiClient = companyApiClient;
            _cache = cache;
        }

        /// Line numbers reported for [date], ascending. Never throws: a
        /// vendor failure yields an empty list, and callers fall back to
        /// what they already know rather than losing the whole page.
        public async Task<List<int>> GetLineNumbersAsync(DateTime date)
        {
            var dateKey = CompanyApiClient.FormatDate(date.Date);
            var cacheKey = $"ProductionLines::{dateKey}";

            if (_cache.TryGetValue(cacheKey, out List<int>? cached) && cached != null)
            {
                return cached;
            }

            var numbers = new SortedSet<int>();
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
                    foreach (var key in row.Operations.Keys)
                    {
                        if (int.TryParse(key, out var n) && n > 0) numbers.Add(n);
                    }
                }
            }
            catch
            {
                // Fall through with whatever was collected - see method doc.
            }

            var result = numbers.ToList();
            _cache.Set(cacheKey, result, CacheTtl);
            return result;
        }
    }
}
