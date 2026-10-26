using System.Globalization;
using Microsoft.Extensions.Logging;
using PA.Connectors.Core;
using PA.Connectors.Core.Catalog;
using PA.Connectors.Core.Jobs;
using PA.Connectors.Core.Pos;
using PA.Connectors.Core.Stores;

namespace PA.Connectors.WooCommerce.Catalog;

/// <summary>
/// S2 (catalog push, design §3): first-connect matching, create-as-draft, incremental
/// price/stock push, nightly reconcile. Handles <see cref="ConnectorJobKinds.CatalogPush"/>
/// (including explicit invNos in a <see cref="CatalogPushPayload"/>) and
/// <see cref="ConnectorJobKinds.CatalogReconcile"/>.
/// <list type="bullet">
/// <item><b>catalog.push, no payload</b>: products <c>modifiedSince</c> the cursor (web products only),
/// unlinked ones created (or linked when Woo already has the SKU), linked ones pushed when the hash of
/// the pushed values changed. The cursor is the job's start time, advanced only after the whole job
/// succeeded, and each read goes back <see cref="ChangeFeedOverlap"/> further (the feed has whole-second,
/// exclusive bounds on the POSitive server's clock); the hash skips what the overlap re-reads.
/// With no cursor yet the job is a full match run (<see cref="RunMatchAsync"/>) instead.</item>
/// <item><b>catalog.push with a payload</b>: exactly those invNos, pushed regardless of cursor and hash.</item>
/// <item><b>catalog.reconcile</b>: full compare of every linked item against the live Woo values, missing
/// Woo products handled, new SKU matches linked, new web items created. The recurring one (resource key
/// <see cref="NightlyResourceKey"/>) is checked hourly and runs once per local day from
/// <see cref="NightlyHour"/>:00; any other resource key runs at once.</item>
/// </list>
/// </summary>
public sealed class WooCatalogSync
{
    /// <summary>ResourceKey of the recurring reconcile; it only does the work once a local day.</summary>
    public const string NightlyResourceKey = "nightly";

    /// <summary>Local hour from which the nightly reconcile runs (it runs at the first hourly check at or after it).</summary>
    public const int NightlyHour = 2;

    /// <summary>Cursor: local date (yyyy-MM-dd) of the last nightly reconcile.</summary>
    public const string NightlyCursor = "catalog.reconcile.lastNightly";

    /// <summary>How far each change-feed read goes back before the cursor (clock skew between the host and SQL, second rounding).</summary>
    public static readonly TimeSpan ChangeFeedOverlap = TimeSpan.FromMinutes(5);

    /// <summary>How often the recurring reconcile job is enqueued to check whether tonight's run is due.</summary>
    public static readonly TimeSpan NightlyCheckInterval = TimeSpan.FromHours(1);

    private readonly ConnectorServices _services;
    private readonly Func<WooClient> _client;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public WooCatalogSync(ConnectorServices services, Func<WooClient> client, TimeProvider? time = null)
    {
        _services = services;
        _client = client;
        _time = time ?? TimeProvider.System;
        _logger = services.LoggerFactory.CreateLogger<WooCatalogSync>();
        RecurringJobs =
        [
            new RecurringJob(ConnectorJobKinds.CatalogPush, TimeSpan.FromSeconds(Math.Max(15, services.Settings.PushIntervalSeconds))),
            new RecurringJob(ConnectorJobKinds.CatalogReconcile, NightlyCheckInterval, NightlyResourceKey),
        ];
    }

    /// <summary>catalog.push every pushIntervalSeconds; catalog.reconcile checked hourly, run nightly.</summary>
    public IReadOnlyList<RecurringJob> RecurringJobs { get; }

    public static bool Handles(string kind) =>
        kind is ConnectorJobKinds.CatalogPush or ConnectorJobKinds.CatalogReconcile;

    private string ConnectorId => _services.Settings.Id;

    public async Task HandleAsync(ConnectorJob job, CancellationToken cancellationToken)
    {
        try
        {
            if (job.Kind == ConnectorJobKinds.CatalogPush)
            {
                var invNos = CatalogPayloads.ReadInvNos(job.Payload);
                if (invNos is null)
                    await PushChangesAsync(cancellationToken);
                else
                    await PushItemsAsync(invNos, cancellationToken);
                return;
            }
            if (job.Kind == ConnectorJobKinds.CatalogReconcile)
            {
                if (job.ResourceKey != NightlyResourceKey)
                {
                    await ReconcileAsync(cancellationToken);
                }
                else if (await NightlyDueAsync(cancellationToken))
                {
                    await ReconcileAsync(cancellationToken);
                    // Recorded after success: a failed run retries through the job's backoff.
                    await _services.Cursors.SetAsync(ConnectorId, NightlyCursor, LocalDate(_time.GetUtcNow().UtcDateTime), cancellationToken);
                }
                return;
            }
            throw new ConnectorJobException($"WooCatalogSync does not handle '{job.Kind}'.", retryable: false);
        }
        catch (WooApiException ex) when (!ex.Transient)
        {
            // 401 (keys revoked), 404 rest_no_route (not Woo any more), invalid JSON: retrying will not help.
            throw new ConnectorJobException($"WooCommerce refused the catalog job: {ex.Message}", retryable: false, ex);
        }
    }

    // --- the S5 UI / job entry points ------------------------------------------

    /// <summary>
    /// The match (first connect; the S5 match review's "run the match"): pages every Woo product and
    /// variation and every POSitive web product, links Woo SKU = POSitive sku or any barcode (case-
    /// insensitive) through PUT /products/{invNo}/link and the item map, raises
    /// <c>catalog_unmatched_woo</c> / <c>catalog_unmatched_pos</c> / <c>catalog_ambiguous</c> for review,
    /// creates the unmatched web items in Woo (status publishNewAs) when <paramref name="createMissing"/>,
    /// then pushes price/stock wherever Woo differs.
    /// </summary>
    public Task<CatalogRunSummary> RunMatchAsync(bool createMissing = true, CancellationToken cancellationToken = default) =>
        RunAsync("match", run => run.FullAsync(raiseReview: true, createMissing, cancellationToken), cancellationToken);

    /// <summary>The nightly full compare-and-fix (also callable on demand).</summary>
    public Task<CatalogRunSummary> ReconcileAsync(CancellationToken cancellationToken = default) =>
        RunAsync("reconcile", run => run.FullAsync(raiseReview: false, createMissing: true, cancellationToken), cancellationToken);

    /// <summary>Creates the given POSitive items in Woo (the match review's "create in Woo"), or links them when Woo already has the SKU.</summary>
    public Task<CatalogRunSummary> CreateInWooAsync(IReadOnlyCollection<int> invNos, CancellationToken cancellationToken = default) =>
        RunAsync("create", async run =>
        {
            var products = await ReadProductsAsync(invNos, run, cancellationToken);
            await run.CreateAsync(products, raiseReview: true, precheck: true, cancellationToken);
        }, cancellationToken);

    /// <summary>catalog.push with a payload: exactly these invNos, regardless of cursor and hash (e.g. after an order import).</summary>
    public Task<CatalogRunSummary> PushItemsAsync(IReadOnlyCollection<int> invNos, CancellationToken cancellationToken = default) =>
        RunAsync("push-items", async run =>
        {
            var products = await ReadProductsAsync(invNos, run, cancellationToken);
            await run.PushAsync(products, WooCatalogRun.PushMode.Force, null, cancellationToken);
        }, cancellationToken);

    /// <summary>catalog.push without a payload: the change feed since the cursor (or the first match when there is none).</summary>
    public async Task<CatalogRunSummary> PushChangesAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = _time.GetUtcNow().UtcDateTime;
        var cursor = ParseUtc(await _services.Cursors.GetAsync(ConnectorId, ConnectorCursorNames.PosModifiedSince, cancellationToken));

        CatalogRunSummary summary;
        if (cursor is null)
        {
            // First push: match everything (review exceptions only when nothing was ever linked).
            summary = await RunAsync("match", async run =>
                await run.FullAsync(raiseReview: run.LinkedCount == 0, createMissing: true, cancellationToken), cancellationToken);
            // The match just compared everything: tonight's reconcile is not needed today.
            await _services.Cursors.SetAsync(ConnectorId, NightlyCursor, LocalDate(startedAt), cancellationToken);
            // The connector pages show it as the last match (the "next steps" after Enable wait for it).
            await _services.Cursors.SetAsync(ConnectorId, ReviewJobKinds.LastRunCursor, System.Text.Json.JsonSerializer.Serialize(new
            {
                jobId = (long?)null,
                kind = ConnectorJobKinds.CatalogPush,
                completedAt = _time.GetUtcNow().UtcDateTime,
                summary,
            }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), cancellationToken);
        }
        else
        {
            summary = await RunAsync("push", async run =>
            {
                var changed = await PosCatalog.ListAllAsync(_services.Pos, new PosProductQuery
                {
                    WebstoreOnly = true,
                    ModifiedSince = cursor.Value - ChangeFeedOverlap,
                }, cancellationToken);
                run.Summary.PosItems = changed.Count;
                await run.CreateAsync(changed, raiseReview: true, precheck: true, cancellationToken);
                await run.PushAsync(changed, WooCatalogRun.PushMode.Changes, null, cancellationToken);
            }, cancellationToken);
        }

        // Only now — every item pushed, or its failure recorded as an exception.
        await _services.Cursors.SetAsync(ConnectorId, ConnectorCursorNames.PosModifiedSince, FormatUtc(startedAt), cancellationToken);
        return summary;
    }

    // --- plumbing ----------------------------------------------------------------

    private async Task<CatalogRunSummary> RunAsync(string mode, Func<WooCatalogRun, Task> work, CancellationToken cancellationToken)
    {
        var run = await WooCatalogRun.StartAsync(_services, _client(), mode, _logger, cancellationToken);
        await work(run);
        await run.CloseResolvedAsync(cancellationToken);
        var level = run.Summary.Pushed + run.Summary.Created + run.Summary.Matched + run.Summary.Missing > 0
            ? LogLevel.Information
            : LogLevel.Debug;
        _logger.Log(level, "Connector {ConnectorId} catalog {Summary}", ConnectorId, run.Summary);
        return run.Summary;
    }

    private async Task<List<PosProduct>> ReadProductsAsync(IReadOnlyCollection<int> invNos, WooCatalogRun run, CancellationToken cancellationToken)
    {
        var products = new List<PosProduct>();
        foreach (var invNo in invNos.Distinct())
        {
            if (await PosCatalog.GetAsync(_services.Pos, invNo, cancellationToken) is { } product)
                products.Add(product);
        }
        run.Summary.PosItems = products.Count;
        return products;
    }

    private async Task<bool> NightlyDueAsync(CancellationToken cancellationToken)
    {
        // Not before the first push has matched the catalog (it compares everything itself).
        if (await _services.Cursors.GetAsync(ConnectorId, ConnectorCursorNames.PosModifiedSince, cancellationToken) is null)
            return false;
        var now = _time.GetUtcNow().UtcDateTime;
        var local = TimeZoneInfo.ConvertTimeFromUtc(now, _time.LocalTimeZone);
        if (local.Hour < NightlyHour)
            return false;
        return await _services.Cursors.GetAsync(ConnectorId, NightlyCursor, cancellationToken) != LocalDate(now);
    }

    private string LocalDate(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _time.LocalTimeZone)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string FormatUtc(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private static DateTime? ParseUtc(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
}
