using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ELibrary.Infrastructure.Doab
{
    /// <summary>
    /// Keeps the local DOAB catalogue current from DOAB's public OAI-PMH feed:
    /// 1. First run: harvests the whole catalogue (~130k records, 100 per page, roughly 3-4 hours).
    ///    Progress is saved after every page, so a restart resumes where it stopped, and search works on
    ///    whatever has been harvested so far.
    /// 2. Afterwards, every <c>DoaBooks:SyncIntervalHours</c>: harvests only records changed since the last run,
    ///    including deletions (usually a page or two).
    /// A failed run is retried after <c>DoaBooks:SyncRetryMinutes</c>.
    /// </summary>
    public sealed class DoabCatalogueSyncService : BackgroundService
    {
        private const string FullHarvestTokenKey = "full_harvest_resumption_token";
        private const string FullHarvestStartedKey = "full_harvest_started_at";
        private const int PageAttempts = 3;
        private static readonly TimeSpan PageRetryDelay = TimeSpan.FromSeconds(30);

        private readonly IServiceScopeFactory _scopes;
        private readonly IDoabCatalogueStore _store;
        private readonly IConfiguration _config;
        private readonly ILogger<DoabCatalogueSyncService> _logger;

        public DoabCatalogueSyncService(IServiceScopeFactory scopes, IDoabCatalogueStore store, IConfiguration config,
            ILogger<DoabCatalogueSyncService> logger)
        {
            _scopes = scopes;
            _store = store;
            _config = config;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_config.GetValue("DoaBooks:SyncEnabled", true))
            {
                _logger.LogInformation("DOAB catalogue sync is disabled (DoaBooks:SyncEnabled=false)");
                return;
            }

            var interval = TimeSpan.FromHours(_config.GetValue("DoaBooks:SyncIntervalHours", 24.0));
            var retryDelay = TimeSpan.FromMinutes(_config.GetValue("DoaBooks:SyncRetryMinutes", 15.0));

            // Don't hold up application startup.
            await Task.Yield();

            while (!stoppingToken.IsCancellationRequested)
            {
                var succeeded = true;
                try
                {
                    if (!_store.IsReady)
                        await HarvestFullCatalogueAsync(stoppingToken);

                    await HarvestChangesAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    succeeded = false;
                    _logger.LogError(ex, "DOAB catalogue sync failed; retrying in {RetryDelay}", retryDelay);
                }

                try
                {
                    await Task.Delay(succeeded ? interval : retryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task HarvestFullCatalogueAsync(CancellationToken stoppingToken)
        {
            var token = NullIfEmpty(_store.GetState(FullHarvestTokenKey));
            if (token == null)
            {
                _store.SetState(FullHarvestStartedKey, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                _logger.LogInformation("DOAB catalogue is empty; starting a full OAI-PMH harvest (takes a few hours, resumable)");
            }
            else
            {
                _logger.LogInformation("Resuming the DOAB full harvest ({Count} items so far)", _store.Count());
            }

            using var scope = _scopes.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<DoabClient>();
            var stopwatch = Stopwatch.StartNew();
            var pages = 0;

            while (true)
            {
                DoabOaiPage page;
                try
                {
                    page = await GetPageWithRetryAsync(client, null, token, stoppingToken);
                }
                catch (DoabOaiException ex) when (ex.Code == "badResumptionToken" && token != null)
                {
                    // The saved position is no longer accepted; start over (already-harvested items are just updated).
                    _logger.LogWarning("DOAB rejected the saved harvest position; restarting the full harvest from the beginning");
                    _store.SetState(FullHarvestTokenKey, string.Empty);
                    token = null;
                    continue;
                }

                _store.Upsert(page.Entries);
                foreach (var handle in page.DeletedHandles)
                    _store.DeleteByHandle(handle);

                if (++pages % 25 == 0)
                {
                    // The OAI cursor counts pages of 100, not records.
                    _logger.LogInformation("DOAB full harvest: page {Page} of ~{TotalPages}, {Count} items stored ({Elapsed} this run)",
                        (page.Cursor ?? pages - 1) + 1, (page.CompleteListSize + 99) / 100, _store.Count(), stopwatch.Elapsed);
                }

                token = page.ResumptionToken;
                if (token == null)
                    break;
                _store.SetState(FullHarvestTokenKey, token);
            }

            // Changes made while the harvest was running are picked up by the first incremental run.
            var startedAt = DateTimeOffset.TryParse(_store.GetState(FullHarvestStartedKey), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var s) ? s : DateTimeOffset.UtcNow;
            _store.SetState(DoabCatalogueStore.NextHarvestFromKey, FormatDate(startedAt.AddDays(-1)));
            _store.SetState(DoabCatalogueStore.BootstrapCompletedKey, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            _store.SetState(FullHarvestTokenKey, string.Empty);
            _logger.LogInformation("DOAB catalogue ready: {Count} items", _store.Count());
        }

        private async Task HarvestChangesAsync(CancellationToken stoppingToken)
        {
            var startedAt = DateTimeOffset.UtcNow;
            var from = DateOnly.TryParseExact(_store.GetState(DoabCatalogueStore.NextHarvestFromKey), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : DateOnly.FromDateTime(startedAt.UtcDateTime.AddDays(-1));

            using var scope = _scopes.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<DoabClient>();

            int pages = 0, upserted = 0, deleted = 0;
            string? token = null;
            do
            {
                var page = await GetPageWithRetryAsync(client, token == null ? from : null, token, stoppingToken);
                _store.Upsert(page.Entries);
                foreach (var handle in page.DeletedHandles)
                    _store.DeleteByHandle(handle);

                pages++;
                upserted += page.Entries.Count;
                deleted += page.DeletedHandles.Count;
                token = page.ResumptionToken;
            } while (token != null);

            // Overlap by a day so nothing slips between runs (OAI-PMH 'from' is inclusive and day-granular here).
            _store.SetState(DoabCatalogueStore.NextHarvestFromKey, FormatDate(startedAt.AddDays(-1)));
            _logger.LogInformation(
                "DOAB catalogue updated from OAI-PMH (changes since {From}): {Upserted} updated, {Deleted} removed, {Pages} pages",
                from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), upserted, deleted, pages);
        }

        private async Task<DoabOaiPage> GetPageWithRetryAsync(DoabClient client, DateOnly? from, string? token, CancellationToken ct)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await client.ListRecordsAsync(from, token, ct);
                }
                catch (Exception ex) when (attempt < PageAttempts && ex is not DoabOaiException && !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "DOAB OAI-PMH page request failed (attempt {Attempt} of {Max}); retrying", attempt, PageAttempts);
                    await Task.Delay(PageRetryDelay, ct);
                }
            }
        }

        private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

        private static string FormatDate(DateTimeOffset value) =>
            DateOnly.FromDateTime(value.UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
