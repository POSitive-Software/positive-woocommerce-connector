using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PA.Connectors.Core;
using PA.Connectors.Core.Catalog;
using PA.Connectors.Core.Jobs;

namespace PA.Connectors.WooCommerce.Catalog;

/// <summary>
/// S5: the jobs the connector UI enqueues for the match review — <c>catalog.match</c> (run the
/// match), <c>catalog.create</c> (create chosen POSitive items in Woo) and <c>catalog.link</c>
/// (link one Woo item to one POSitive item by hand, then push it). They run on the worker like
/// every other job, so a UI action never races a push. Each run's summary is kept in the cursor
/// <see cref="ReviewJobKinds.LastRunCursor"/> for the page to show.
/// </summary>
public sealed class WooCatalogReviewJobs
{
    private readonly ConnectorServices _services;
    private readonly WooCatalogSync _catalog;
    private readonly Func<WooClient> _client;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public WooCatalogReviewJobs(ConnectorServices services, WooCatalogSync catalog, Func<WooClient> client, TimeProvider? time = null)
    {
        _services = services;
        _catalog = catalog;
        _client = client;
        _time = time ?? TimeProvider.System;
        _logger = services.LoggerFactory.CreateLogger<WooCatalogReviewJobs>();
    }

    private string ConnectorId => _services.Settings.Id;

    public async Task HandleAsync(ConnectorJob job, CancellationToken cancellationToken)
    {
        try
        {
            CatalogRunSummary summary;
            switch (job.Kind)
            {
                case ReviewJobKinds.CatalogMatch:
                    var match = ReviewJobKinds.Read<CatalogMatchPayload>(job.Payload) ?? new CatalogMatchPayload();
                    summary = await _catalog.RunMatchAsync(match.CreateMissing, cancellationToken);
                    break;
                case ReviewJobKinds.CatalogCreate:
                    var create = ReviewJobKinds.Read<CatalogCreatePayload>(job.Payload);
                    if (create?.InvNos is not { Count: > 0 })
                        throw new ConnectorJobException("catalog.create needs a payload {\"invNos\":[…]}.", retryable: false);
                    summary = await _catalog.CreateInWooAsync(create.InvNos, cancellationToken);
                    break;
                case ReviewJobKinds.CatalogLink:
                    var link = ReviewJobKinds.Read<CatalogLinkPayload>(job.Payload);
                    if (link is null || link.InvNo <= 0 || link.ExternalId <= 0)
                        throw new ConnectorJobException("catalog.link needs a payload {\"invNo\":…,\"externalId\":…}.", retryable: false);
                    summary = await LinkAsync(link, cancellationToken);
                    break;
                default:
                    throw new ConnectorJobException($"WooCatalogReviewJobs does not handle '{job.Kind}'.", retryable: false);
            }

            await _services.Cursors.SetAsync(ConnectorId, ReviewJobKinds.LastRunCursor, JsonSerializer.Serialize(new
            {
                jobId = job.JobId,
                kind = job.Kind,
                completedAt = _time.GetUtcNow().UtcDateTime,
                summary,
            }, ReviewJson), cancellationToken);
        }
        catch (WooApiException ex) when (!ex.Transient)
        {
            throw new ConnectorJobException($"WooCommerce refused the {job.Kind} job: {ex.Message}", retryable: false, ex);
        }
    }

    private async Task<CatalogRunSummary> LinkAsync(CatalogLinkPayload link, CancellationToken cancellationToken)
    {
        var run = await WooCatalogRun.StartAsync(_services, _client(), "link", _logger, cancellationToken);
        if (await run.LinkManualAsync(link.InvNo, link.ExternalId, cancellationToken, link.ReplaceExisting))
        {
            // Woo now gets POSitive's price and stock for it straight away (hash and cursor ignored).
            if (await PosCatalog.GetAsync(_services.Pos, link.InvNo, cancellationToken) is { } product)
                await run.PushAsync([product], WooCatalogRun.PushMode.Force, null, cancellationToken);
            if (link.RetryOrderId is > 0)
                await _services.Jobs.EnqueueAsync(new JobRequest(ConnectorId, ConnectorJobKinds.OrderImport,
                    link.RetryOrderId.Value.ToString(CultureInfo.InvariantCulture),
                    ReviewJobKinds.Serialize(new { source = "ui-link", invNo = link.InvNo })), cancellationToken);
        }
        await run.CloseResolvedAsync(cancellationToken);
        _logger.LogInformation("Connector {ConnectorId} catalog {Summary}", ConnectorId, run.Summary);
        return run.Summary;
    }

    private static readonly JsonSerializerOptions ReviewJson = new(JsonSerializerDefaults.Web);
}
