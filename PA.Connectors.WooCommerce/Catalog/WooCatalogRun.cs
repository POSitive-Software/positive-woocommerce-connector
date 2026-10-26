using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PA.Connectors.Core;
using PA.Connectors.Core.Catalog;
using PA.Connectors.Core.Pos;
using PA.Connectors.Core.Stores;
using PA.Connectors.WooCommerce.Models;

namespace PA.Connectors.WooCommerce.Catalog;

/// <summary>
/// The work of ONE catalog job: the item map loaded once, the store's decimals read
/// once, and the match / create / push / missing steps the job kinds combine. Items are
/// processed in batches of ≤100 (WooClient chunks); each item's error becomes an
/// exception row and the rest carries on. A transient fault (Woo 429/5xx, POSitive
/// down) propagates, so the job retries — hashes already saved make the retry skip
/// what was done.
/// </summary>
internal sealed class WooCatalogRun
{
    private readonly ConnectorServices _services;
    private readonly WooClient _woo;
    private readonly ILogger _logger;
    private readonly Dictionary<int, ConnectorItem> _byInv;
    private readonly Dictionary<long, ConnectorItem> _byWoo;
    private readonly HashSet<(string Kind, string Ref)> _resolved = [];
    private readonly IReadOnlyDictionary<int, decimal> _held;

    private WooCatalogRun(ConnectorServices services, WooClient woo, int decimals, IEnumerable<ConnectorItem> items,
        IReadOnlyDictionary<int, decimal> held, CatalogRunSummary summary, ILogger logger)
    {
        _services = services;
        _woo = woo;
        Decimals = decimals;
        Summary = summary;
        _logger = logger;
        _held = held;
        _byInv = items.ToDictionary(i => i.InvNo);
        _byWoo = _byInv.Values.GroupBy(i => i.ExternalId).ToDictionary(g => g.Key, g => g.First());
    }

    public int Decimals { get; }

    public CatalogRunSummary Summary { get; }

    public int LinkedCount => _byInv.Values.Count(i => i.ExternalType != CatalogItemTypes.Missing);

    private ConnectorSettings Settings => _services.Settings;

    private string ConnectorId => Settings.Id;

    private IPosGateway Pos => _services.Pos;

    private bool OverwriteText => Settings.OverwriteTitleDescription;

    public static async Task<WooCatalogRun> StartAsync(
        ConnectorServices services, WooClient woo, string mode, ILogger logger, CancellationToken cancellationToken)
    {
        int decimals;
        try
        {
            decimals = await woo.GetPriceDecimalsAsync(cancellationToken) ?? CatalogMoney.DefaultDecimals;
        }
        catch (WooApiException ex) when (!ex.Transient)
        {
            logger.LogWarning("Could not read the store's price decimals ({Error}); using {Decimals}", ex.Message, CatalogMoney.DefaultDecimals);
            decimals = CatalogMoney.DefaultDecimals;
        }
        var items = await services.Items.ListAsync(services.Settings.Id, cancellationToken);
        // Woo orders that hold Woo stock but are not in POSitive: every push in this run subtracts them.
        var held = await services.Orders.GetStockHoldTotalsAsync(services.Settings.Id, cancellationToken);
        return new WooCatalogRun(services, woo, decimals, items, held, new CatalogRunSummary { Mode = mode }, logger);
    }

    // --- full run: match + create + compare-and-push --------------------------

    /// <summary>
    /// Match (first connect, the S5 review, and nightly), then create the unmatched web items,
    /// then push every linked web item whose Woo values differ from POSitive's.
    /// <paramref name="raiseReview"/> = raise the review exceptions (unmatched / ambiguous);
    /// the nightly reconcile links and creates silently so an ignored row is not re-opened every night.
    /// </summary>
    public async Task FullAsync(bool raiseReview, bool createMissing, CancellationToken cancellationToken)
    {
        var snapshot = await WooSnapshot.LoadAsync(_woo, OverwriteText, cancellationToken);
        var posWeb = (await PosCatalog.ListAllAsync(Pos, new PosProductQuery { WebstoreOnly = true }, cancellationToken))
            .ToDictionary(p => p.InvNo);
        Summary.WooItems = snapshot.Linkable.Count();
        Summary.PosItems = posWeb.Count;

        // 1. Linked items whose Woo product is gone (or restored from the trash).
        foreach (var item in _byInv.Values.ToList())
        {
            var present = snapshot.ById.TryGetValue(item.ExternalId, out var woo) && woo.Linkable;
            if (item.ExternalType == CatalogItemTypes.Missing)
            {
                if (present && await LinkAsync(item.InvNo, woo!, item.Sku, raiseOnFailure: true, cancellationToken))
                {
                    Summary.Relinked++;
                    _resolved.Add((CatalogExceptionKinds.PlatformMissing, Ref(item)));
                }
                continue;
            }
            if (!present)
                await HandleMissingAsync(item, snapshot, allowRelink: true, cancellationToken);
        }

        // 2. Match the rest by SKU / barcode.
        var toCreate = await MatchAsync(snapshot, posWeb, raiseReview, cancellationToken);

        // 3. Create what Woo does not have.
        if (createMissing)
            await CreateAsync(toCreate, raiseReview, precheck: false, cancellationToken);

        // 4. Push what differs from the live Woo values.
        await PushAsync(posWeb.Values, PushMode.Compare, snapshot, cancellationToken);
    }

    // --- matching ------------------------------------------------------------

    /// <summary>Links Woo items to POSitive web items; returns the web items left for creation.</summary>
    private async Task<List<PosProduct>> MatchAsync(
        WooSnapshot snapshot, Dictionary<int, PosProduct> posWeb, bool raise, CancellationToken cancellationToken)
    {
        var linkedInv = _byInv.Values.Where(i => i.ExternalType != CatalogItemTypes.Missing).Select(i => i.InvNo).ToHashSet();
        var candidates = snapshot.Linkable.Where(w => !_byWoo.ContainsKey(w.Id)).ToList();
        var candidateById = candidates.ToDictionary(w => w.Id);
        var open = posWeb.Values.Where(p => !linkedInv.Contains(p.InvNo)).ToDictionary(p => p.InvNo);
        var proposals = new Dictionary<long, int>();
        var blocked = new HashSet<int>();

        // a) The item already carries this tenant's link to a Woo id (an earlier install, or the map was lost).
        foreach (var product in open.Values)
        {
            if (string.IsNullOrWhiteSpace(product.ExternalId))
                continue;
            if (long.TryParse(product.ExternalId.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var wooId)
                && candidateById.ContainsKey(wooId) && !proposals.ContainsKey(wooId))
            {
                proposals[wooId] = product.InvNo;
                continue;
            }
            // Linked to something that is not a product of this store: linking another id would be link_exists.
            blocked.Add(product.InvNo);
            await RaiseAsync(CatalogExceptionKinds.LinkConflict, CatalogExceptionKinds.PosRef(product.InvNo), product.InvNo,
                $"POSitive item {product.InvNo} ({product.Sku}) is already linked to '{product.ExternalId}' for this connector's key, which is not a product or variation in the store. Unlink it (DELETE /api/v2/products/{product.InvNo}/link) to let the connector match or create it.",
                cancellationToken);
            Summary.LinkFailed++;
        }

        // b) Woo SKU = POSitive sku, else any barcode of the item (BARCODES, via ?sku=). Case-insensitive.
        var proposedInv = proposals.Values.ToHashSet();
        var posBySku = open.Values
            .Where(p => !proposedInv.Contains(p.InvNo) && !blocked.Contains(p.InvNo) && CatalogMapper.SkuKey(p.Sku) is not null)
            .GroupBy(p => CatalogMapper.SkuKey(p.Sku)!)
            .ToDictionary(g => g.Key, g => g.Select(p => p.InvNo).ToList());

        foreach (var woo in candidates.Where(w => !proposals.ContainsKey(w.Id)))
        {
            var key = CatalogMapper.SkuKey(woo.Sku);
            if (key is null)
            {
                await UnmatchedWooAsync(woo, $"Woo {Describe(woo)} has no SKU, so it cannot be matched; it stays unmanaged.", raise, cancellationToken);
                continue;
            }

            var sameSku = snapshot.WithSku(key);
            if (sameSku.Count > 1)
            {
                await AmbiguousAsync(woo.Id.ToString(CultureInfo.InvariantCulture), null,
                    $"SKU '{woo.Sku}' is used by {sameSku.Count} Woo products ({string.Join(", ", sameSku.Select(Describe))}); none is linked.",
                    raise, cancellationToken);
                if (posBySku.Remove(key, out var held))
                    blocked.UnionWith(held);
                continue;
            }

            var hits = posBySku.TryGetValue(key, out var bySku) ? bySku : [];
            if (hits.Count == 0)
            {
                var resolved = await Pos.ResolveSkuAsync(woo.Sku!, cancellationToken);
                hits = resolved.Where(i => open.ContainsKey(i) && !proposedInv.Contains(i) && !blocked.Contains(i)).Distinct().ToList();
                if (hits.Count == 0)
                {
                    var elsewhere = resolved.Where(linkedInv.Contains).ToList();
                    var notWeb = resolved.Where(i => !posWeb.ContainsKey(i)).ToList();
                    var reason = elsewhere.Count > 0
                        ? $"SKU '{woo.Sku}' matches POSitive item {Join(elsewhere)}, which is already linked to another Woo product."
                        : notWeb.Count > 0
                            ? $"SKU '{woo.Sku}' matches POSitive item {Join(notWeb)}, which is not flagged as a web product."
                            : $"No POSitive web item has SKU or barcode '{woo.Sku}'; it stays unmanaged.";
                    await UnmatchedWooAsync(woo, reason, raise, cancellationToken);
                    continue;
                }
            }

            if (hits.Count > 1)
            {
                await AmbiguousAsync(woo.Id.ToString(CultureInfo.InvariantCulture), null,
                    $"SKU '{woo.Sku}' of Woo {Describe(woo)} matches POSitive items {Join(hits)}; none is linked.", raise, cancellationToken);
                blocked.UnionWith(hits);
                continue;
            }
            proposals[woo.Id] = hits[0];
        }

        // c) One POSitive item claimed by several Woo items (e.g. by two barcodes): link none of them.
        foreach (var group in proposals.GroupBy(p => p.Value).Where(g => g.Count() > 1).ToList())
        {
            var woos = group.Select(p => candidateById[p.Key]).ToList();
            foreach (var woo in woos)
            {
                proposals.Remove(woo.Id);
                await AmbiguousAsync(woo.Id.ToString(CultureInfo.InvariantCulture), group.Key,
                    $"POSitive item {group.Key} matches {woos.Count} Woo products ({string.Join(", ", woos.Select(Describe))}); none is linked.",
                    raise, cancellationToken);
            }
            blocked.Add(group.Key);
        }

        foreach (var (wooId, invNo) in proposals)
        {
            if (await LinkAsync(invNo, candidateById[wooId], posWeb[invNo].Sku, raiseOnFailure: true, cancellationToken))
                Summary.Matched++;
        }

        // What is left on the POSitive side: create it, unless its SKU is held by any Woo product
        // (a variable parent, grouped or external product, or an ambiguous one).
        var toCreate = new List<PosProduct>();
        foreach (var product in open.Values.OrderBy(p => p.InvNo))
        {
            if (_byInv.ContainsKey(product.InvNo) || blocked.Contains(product.InvNo))
                continue;
            var held = snapshot.WithSku(CatalogMapper.SkuKey(product.Sku));
            if (held.Count > 0)
            {
                await UnmatchedPosAsync(product,
                    $"POSitive item {product.InvNo}: SKU '{product.Sku}' is already used in Woo by {string.Join(", ", held.Select(Describe))}, which the connector does not link; it is not created.",
                    raise, cancellationToken);
                continue;
            }
            toCreate.Add(product);
        }
        return toCreate;
    }

    // --- create ----------------------------------------------------------------

    /// <summary>
    /// Creates POSitive web items in Woo as simple products (status = publishNewAs, default draft),
    /// with name and description, SKU, prices and stock; then maps and links them. Matrix items are
    /// not created (design §9.5). <paramref name="precheck"/> = look the SKUs up in Woo first and link
    /// instead of creating — used outside a full run, where no fresh snapshot proves them absent.
    /// </summary>
    public async Task CreateAsync(IReadOnlyCollection<PosProduct> products, bool raiseReview, bool precheck, CancellationToken cancellationToken)
    {
        var pending = products.Where(p => !_byInv.ContainsKey(p.InvNo)).GroupBy(p => p.InvNo).Select(g => g.First()).ToList();
        if (pending.Count == 0)
            return;

        var eligible = new List<PosProduct>();
        foreach (var product in pending)
        {
            if (CatalogMapper.SkuKey(product.Sku) is null)
            {
                // Without a SKU nothing ties the new Woo product back to the item if the map is lost,
                // and a retried create cannot find the one an earlier attempt made: review it instead.
                await UnmatchedPosAsync(product,
                    $"POSitive item {product.InvNo} ({CatalogMapper.NameOf(product)}) has no SKU, so it is not created in Woo. Give it a SKU (or barcode) in POSitive, or create and link it by hand.",
                    raiseReview, cancellationToken, group: "no_sku");
                continue;
            }
            if (CatalogMapper.IsMatrix(product))
            {
                await UnmatchedPosAsync(product,
                    $"POSitive item {product.InvNo} ({product.Sku}) is a matrix item. The connector links existing Woo variations by SKU but does not create variable products: create it in Woo with the same SKUs, then run the match.",
                    raiseReview, cancellationToken, group: "matrix");
                continue;
            }
            eligible.Add(product);
        }

        // Two web items with one SKU cannot both exist in Woo (SKUs are unique there).
        foreach (var dup in eligible.Where(p => CatalogMapper.SkuKey(p.Sku) is not null)
                     .GroupBy(p => CatalogMapper.SkuKey(p.Sku)!).Where(g => g.Count() > 1).ToList())
        {
            foreach (var product in dup)
            {
                eligible.Remove(product);
                await AmbiguousAsync(CatalogExceptionKinds.PosRef(product.InvNo), product.InvNo,
                    $"POSitive web items {Join(dup.Select(p => p.InvNo))} share SKU '{product.Sku}'; none is created in Woo.",
                    raiseReview, cancellationToken);
            }
        }

        if (precheck && eligible.Count > 0)
            eligible = await LinkExistingBySkuAsync(eligible, raiseReview, cancellationToken);

        var plans = new List<(PosProduct Product, CatalogPushValues Values, WooProductWrite Write)>();
        foreach (var product in eligible)
        {
            var mapped = CatalogMapper.Map(product, Settings, Decimals, includeText: true, Held(product.InvNo));
            if (!mapped.Success)
            {
                Summary.CreateFailed++;
                await RaiseAsync(CatalogExceptionKinds.CreateFailed, CatalogExceptionKinds.PosRef(product.InvNo), product.InvNo,
                    mapped.Error!, cancellationToken);
                continue;
            }
            var values = mapped.Values!;
            plans.Add((product, values, new WooProductWrite
            {
                Name = values.Name,
                Type = WooProductTypes.Simple,
                Status = Settings.PublishNewAs,
                Sku = string.IsNullOrWhiteSpace(product.Sku) ? null : product.Sku.Trim(),
                RegularPrice = CatalogMoney.Format(values.RegularPrice, Decimals),
                SalePrice = values.SalePrice is { } sale ? CatalogMoney.Format(sale, Decimals) : null,
                ManageStock = true,
                StockQuantity = values.StockQuantity,
                Description = values.Description,
            }));
        }

        // Results are matched back by SKU (every created item has one).
        foreach (var chunk in plans.Chunk(WooClient.MaxBatchSize))
        {
            var result = await _woo.BatchProductsAsync(
                new WooBatchRequest { Create = chunk.Select(c => c.Write).ToList() }, cancellationToken);

            var created = new List<WooBatchItem>(result.Created);
            var failed = new List<(PosProduct Product, CatalogPushValues Values)>();
            foreach (var (product, values, write) in chunk)
            {
                var hit = created.FirstOrDefault(c => CatalogMapper.SkuKey(c.Sku) == CatalogMapper.SkuKey(write.Sku));
                if (hit is null || hit.Id <= 0)
                {
                    failed.Add((product, values));
                    continue;
                }
                created.Remove(hit);
                Summary.Created++;
                var woo = new WooItem(hit.Id, 0, CatalogItemTypes.Simple, write.Sku, Settings.PublishNewAs,
                    values.RegularPrice, values.SalePrice, true, values.StockQuantity, values.Name, values.Description);
                var hash = values.Hash(Decimals, OverwriteText);
                if (await LinkAsync(product.InvNo, woo, product.Sku, raiseOnFailure: true, cancellationToken, hash))
                    _resolved.Add((CatalogExceptionKinds.CreateFailed, CatalogExceptionKinds.PosRef(product.InvNo)));
            }

            // Woo answers in request order, so the failures line up with the errors in order.
            for (var i = 0; i < failed.Count; i++)
            {
                var error = i < result.Errors.Count ? result.Errors[i] : null;
                Summary.CreateFailed++;
                await RaiseAsync(CatalogExceptionKinds.CreateFailed, CatalogExceptionKinds.PosRef(failed[i].Product.InvNo),
                    failed[i].Product.InvNo,
                    $"Creating POSitive item {failed[i].Product.InvNo} ({failed[i].Product.Sku}) in Woo failed: " +
                    (error is null ? "the store did not return the new product." : $"{error.Code}: {error.Message}"),
                    cancellationToken);
            }
        }
    }

    /// <summary>Outside a full run: web items whose SKU Woo already has are linked (or reported), not created.</summary>
    private async Task<List<PosProduct>> LinkExistingBySkuAsync(
        List<PosProduct> products, bool raiseReview, CancellationToken cancellationToken)
    {
        var found = (await _woo.FindBySkusAsync(products.Select(p => p.Sku ?? "").Where(s => s.Length > 0), cancellationToken))
            .Select(WooItem.From).Where(w => !w.Trashed).ToList();
        if (found.Count == 0)
            return products;
        var byKey = found.GroupBy(w => CatalogMapper.SkuKey(w.Sku)!).ToDictionary(g => g.Key, g => g.ToList());

        var rest = new List<PosProduct>();
        foreach (var product in products)
        {
            var key = CatalogMapper.SkuKey(product.Sku);
            if (key is null || !byKey.TryGetValue(key, out var hits))
            {
                rest.Add(product);
                continue;
            }
            if (hits.Count == 1 && hits[0].Linkable && !_byWoo.ContainsKey(hits[0].Id))
            {
                if (await LinkAsync(product.InvNo, hits[0], product.Sku, raiseOnFailure: true, cancellationToken))
                    Summary.Matched++;
                continue;
            }
            await AmbiguousAsync(CatalogExceptionKinds.PosRef(product.InvNo), product.InvNo,
                $"POSitive item {product.InvNo}: SKU '{product.Sku}' is already used in Woo by {string.Join(", ", hits.Select(Describe))}, which cannot be linked to it; it is not created.",
                raiseReview, cancellationToken);
        }
        return rest;
    }

    // --- push ----------------------------------------------------------------

    public enum PushMode
    {
        /// <summary>Skip an item whose values hash equals the last push (the change feed).</summary>
        Changes,

        /// <summary>Push regardless of the hash (explicit invNos after an order: Woo may have moved on its own).</summary>
        Force,

        /// <summary>Compare with the live Woo values from the snapshot (reconcile); hash when not in the snapshot.</summary>
        Compare,
    }

    private sealed record PushPlan(ConnectorItem Item, PosProduct Product, string Hash, WooProductWrite Write);

    /// <summary>
    /// Pushes price and stock (plus title/description when overwriteTitleDescription) for every
    /// linked item among <paramref name="products"/>: products/batch for simple products,
    /// products/{parent}/variations/batch per variable parent, ≤100 per request.
    /// </summary>
    public async Task PushAsync(IEnumerable<PosProduct> products, PushMode mode, WooSnapshot? snapshot,
        CancellationToken cancellationToken, bool allowRelink = true)
    {
        var plans = new List<PushPlan>();
        foreach (var product in products)
        {
            if (!_byInv.TryGetValue(product.InvNo, out var item) || item.ExternalType == CatalogItemTypes.Missing)
                continue;
            var isVariation = item.ExternalType == CatalogItemTypes.Variation;
            var mapped = CatalogMapper.Map(product, Settings, Decimals, OverwriteText, Held(product.InvNo));
            if (!mapped.Success)
            {
                Summary.PushFailed++;
                await RaiseAsync(ConnectorExceptionKinds.PushFailed, Ref(item), item.InvNo, mapped.Error!, cancellationToken);
                continue;
            }
            var values = mapped.Values!;
            var hash = values.Hash(Decimals, OverwriteText);

            if (mode == PushMode.Compare && snapshot is not null && snapshot.ById.TryGetValue(item.ExternalId, out var live))
            {
                if (InSync(live, values, isVariation))
                {
                    if (hash != item.LastPushHash)
                        await SetHashAsync(item, hash, cancellationToken);
                    Summary.Unchanged++;
                    continue;
                }
            }
            else if (mode != PushMode.Force && hash == item.LastPushHash)
            {
                Summary.Unchanged++;
                continue;
            }

            plans.Add(new PushPlan(item, product, hash, new WooProductWrite
            {
                Id = item.ExternalId,
                RegularPrice = CatalogMoney.Format(values.RegularPrice, Decimals),
                SalePrice = values.SalePrice is { } sale ? CatalogMoney.Format(sale, Decimals) : "",
                ManageStock = true,
                StockQuantity = values.StockQuantity,
                // A variation's name comes from its attributes; only its description is its own.
                Name = isVariation ? null : values.Name,
                Description = values.Description,
            }));
        }
        if (plans.Count == 0)
            return;

        var missing = new List<PushPlan>();
        var simple = plans.Where(p => p.Item.ExternalType != CatalogItemTypes.Variation).ToList();
        if (simple.Count > 0)
            await SendAsync(simple, null, missing, cancellationToken);
        foreach (var group in plans.Where(p => p.Item.ExternalType == CatalogItemTypes.Variation).GroupBy(p => p.Item.ExternalParentId))
            await SendAsync(group.ToList(), group.Key, missing, cancellationToken);

        var relinked = new List<PosProduct>();
        foreach (var plan in missing)
        {
            if (await HandleMissingAsync(plan.Item, null, allowRelink, cancellationToken))
                relinked.Add(plan.Product);
        }
        // An item moved to its re-made Woo product is pushed straight away (once — no loop).
        if (relinked.Count > 0)
            await PushAsync(relinked, PushMode.Force, null, cancellationToken, allowRelink: false);
    }

    private async Task SendAsync(List<PushPlan> plans, long? parentId, List<PushPlan> missing, CancellationToken cancellationToken)
    {
        var request = new WooBatchRequest { Update = plans.Select(p => p.Write).ToList() };
        WooBatchResult result;
        try
        {
            result = parentId is { } parent
                ? await _woo.BatchVariationsAsync(parent, request, cancellationToken)
                : await _woo.BatchProductsAsync(request, cancellationToken);
        }
        catch (WooApiException ex) when (parentId is not null && !ex.Transient && ex.Status == HttpStatusCode.NotFound)
        {
            // The variable parent itself is gone: every variation under it is.
            missing.AddRange(plans);
            return;
        }

        var updated = result.Updated.Select(u => u.Id).ToHashSet();
        var errors = result.Errors.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var plan in plans)
        {
            if (updated.Contains(plan.Item.ExternalId))
            {
                Summary.Pushed++;
                await SetHashAsync(plan.Item, plan.Hash, cancellationToken);
                _resolved.Add((ConnectorExceptionKinds.PushFailed, Ref(plan.Item)));
                continue;
            }
            if (errors.TryGetValue(plan.Item.ExternalId, out var error) && IsMissingCode(error.Code))
            {
                missing.Add(plan);
                continue;
            }
            Summary.PushFailed++;
            await RaiseAsync(ConnectorExceptionKinds.PushFailed, Ref(plan.Item), plan.Item.InvNo,
                $"Pushing POSitive item {plan.Item.InvNo} to Woo {DescribeItem(plan.Item)} failed: " +
                (error is null ? "the store did not report the update." : $"{error.Code}: {error.Message}"),
                cancellationToken);
        }
    }

    /// <summary>Woo's per-item "no such product/variation" codes (products/batch, variations/batch).</summary>
    internal static bool IsMissingCode(string? code) =>
        code is not null && (code.EndsWith("_invalid_id", StringComparison.OrdinalIgnoreCase)
                             || code.Contains("invalid_product_id", StringComparison.OrdinalIgnoreCase));

    private bool InSync(WooItem live, CatalogPushValues values, bool isVariation)
    {
        if (!live.ManageStock || live.StockQuantity != values.StockQuantity)
            return false;
        if (live.RegularPrice is not { } regular || CatalogMoney.Round(regular, Decimals) != values.RegularPrice)
            return false;
        var liveSale = live.SalePrice is { } s && s > 0 ? CatalogMoney.Round(s, Decimals) : (decimal?)null;
        if (liveSale != values.SalePrice)
            return false;
        if (!OverwriteText)
            return true;
        return (isVariation || values.Name is null || string.Equals(live.Name?.Trim(), values.Name, StringComparison.Ordinal))
               && (values.Description is null || string.Equals(live.Description?.Trim(), values.Description, StringComparison.Ordinal));
    }

    // --- missing Woo products -------------------------------------------------

    /// <summary>
    /// A linked Woo product is gone. Confirm it (a listing can miss it), then: if exactly one other
    /// unlinked Woo item has the SKU (deleted and re-made), move the link there; otherwise unlink
    /// the item, keep its map row as <see cref="CatalogItemTypes.Missing"/> (so it is neither pushed
    /// nor re-created behind the owner's back) and raise <c>catalog_woo_missing</c>.
    /// True = relinked.
    /// </summary>
    private async Task<bool> HandleMissingAsync(ConnectorItem item, WooSnapshot? snapshot, bool allowRelink, CancellationToken cancellationToken)
    {
        var current = item.ExternalType == CatalogItemTypes.Variation
            ? (await _woo.GetVariationAsync(item.ExternalParentId, item.ExternalId, cancellationToken)) is { } v
                ? WooItem.From(v, item.ExternalParentId)
                : null
            : (await _woo.GetProductAsync(item.ExternalId, cancellationToken)) is { } p
                ? WooItem.From(p)
                : null;
        if (current is { Trashed: false })
            return false; // still there (e.g. a status the listing left out): nothing to fix

        var key = CatalogMapper.SkuKey(item.Sku);
        if (key is not null && allowRelink)
        {
            var sameSku = snapshot is not null
                ? snapshot.WithSku(key)
                : (await _woo.FindBySkusAsync([item.Sku!], cancellationToken)).Select(WooItem.From).Where(w => !w.Trashed).ToList();
            var replacements = sameSku.Where(w => w.Linkable && w.Id != item.ExternalId && !_byWoo.ContainsKey(w.Id)).ToList();
            if (replacements.Count == 1 && sameSku.Count == 1)
            {
                await Pos.UnlinkProductAsync(item.InvNo, cancellationToken);
                if (await LinkAsync(item.InvNo, replacements[0], item.Sku, raiseOnFailure: true, cancellationToken))
                {
                    Summary.Relinked++;
                    _resolved.Add((CatalogExceptionKinds.PlatformMissing, Ref(item)));
                    return true;
                }
            }
        }

        var unlink = await Pos.UnlinkProductAsync(item.InvNo, cancellationToken);
        var missingRow = item with { ExternalType = CatalogItemTypes.Missing };
        await _services.Items.UpsertAsync(missingRow, cancellationToken);
        _byInv[item.InvNo] = missingRow;
        _byWoo[item.ExternalId] = missingRow;
        Summary.Missing++;
        await RaiseAsync(CatalogExceptionKinds.PlatformMissing, Ref(item), item.InvNo,
            $"Woo {DescribeItem(item)} (SKU '{item.Sku}') no longer exists, so POSitive item {item.InvNo} is no longer pushed" +
            (unlink.Success ? " and its link was removed." : $" (removing its link failed: {unlink.Describe()}).") +
            " Re-create or relink it in the match review; it is not re-created automatically.",
            cancellationToken);
        return false;
    }

    // --- link + map -------------------------------------------------------------

    /// <summary>
    /// S5 match review / exceptions page: link Woo product or variation <paramref name="wooId"/> to
    /// POSitive item <paramref name="invNo"/> by hand (Woo answers GET products/{id} for variations too).
    /// Refused — raised as <c>catalog_link_refused</c> (an exception: someone asked for it) on the Woo
    /// id, nothing changed — when the Woo item does not exist or is not a simple product / variation,
    /// or either side is already linked to something else and <paramref name="replaceExisting"/> is
    /// off (a hand link never silently moves an existing one). With <paramref name="replaceExisting"/>
    /// (the owner confirmed "unlink it first") whatever either side is linked to is unlinked first —
    /// in POSitive and in the item map — then the link is made.
    /// </summary>
    public async Task<bool> LinkManualAsync(int invNo, long wooId, CancellationToken cancellationToken, bool replaceExisting = false)
    {
        var wooRef = wooId.ToString(CultureInfo.InvariantCulture);
        var product = await _woo.GetProductAsync(wooId, cancellationToken);
        var woo = product is null ? null : WooItem.From(product);
        // A store that does not answer products/{id} for a variation: find it among every product's variations.
        if (woo is null && (await WooSnapshot.LoadAsync(_woo, withText: false, cancellationToken)).ById.TryGetValue(wooId, out var found))
            woo = found;
        string? problem = woo switch
        {
            null => $"Woo has no product or variation #{wooId}.",
            { Trashed: true } => $"Woo product #{wooId} is in the trash.",
            { Linkable: false } => $"Woo product #{wooId} is a {woo.Kind} product; only simple products and variations are linked.",
            _ when replaceExisting => null,
            _ when _byWoo.TryGetValue(wooId, out var other) && other.InvNo != invNo =>
                $"Woo {Describe(woo)} is already linked to POSitive item {other.InvNo}.",
            _ when _byInv.TryGetValue(invNo, out var mine) && mine.ExternalId != wooId && mine.ExternalType != CatalogItemTypes.Missing =>
                $"POSitive item {invNo} is already linked to Woo {DescribeItem(mine)}.",
            _ => null,
        };
        if (problem is null && replaceExisting)
            problem = await UnlinkConflictsAsync(invNo, wooId, cancellationToken);
        if (problem is not null)
        {
            Summary.LinkFailed++;
            await RaiseAsync(CatalogExceptionKinds.ManualLinkRefused, wooRef, invNo,
                $"Linking POSitive item {invNo} to Woo item #{wooId} by hand was refused: {problem}", cancellationToken);
            return false;
        }

        if (!await LinkAsync(invNo, woo!, woo!.Sku, raiseOnFailure: true, cancellationToken,
                refusedKind: CatalogExceptionKinds.ManualLinkRefused))
            return false;
        Summary.Matched++;
        return true;
    }

    /// <summary>
    /// Before a confirmed replacing link: unlinks whatever <paramref name="invNo"/> and Woo item
    /// <paramref name="wooId"/> are linked to now (other than each other), in POSitive and in the item
    /// map. The Woo product that loses its link stays in Woo as it is (no longer pushed). Null = done;
    /// otherwise why it could not be unlinked (nothing more is changed).
    /// </summary>
    private async Task<string?> UnlinkConflictsAsync(int invNo, long wooId, CancellationToken cancellationToken)
    {
        var wooRef = wooId.ToString(CultureInfo.InvariantCulture);
        var stale = new List<int>();
        if (_byWoo.TryGetValue(wooId, out var other) && other.InvNo != invNo)
            stale.Add(other.InvNo);
        if (_byInv.TryGetValue(invNo, out var mine) && mine.ExternalId != wooId)
            stale.Add(invNo);
        // Links POSitive holds that the item map does not know (an earlier install, a lost map).
        if (await Pos.FindLinkedProductAsync(wooRef, cancellationToken) is { } holder && holder != invNo)
            stale.Add(holder);
        if (!stale.Contains(invNo) && await PosCatalog.GetAsync(Pos, invNo, cancellationToken) is { ExternalId: { Length: > 0 } current }
            && !string.Equals(current.Trim(), wooRef, StringComparison.Ordinal))
            stale.Add(invNo);

        foreach (var staleInv in stale.Distinct())
        {
            var unlinked = await Pos.UnlinkProductAsync(staleInv, cancellationToken);
            if (!unlinked.Success)
                return $"POSitive item {staleInv} could not be unlinked first: {unlinked.Describe()}";
            if (_byInv.Remove(staleInv, out var row))
            {
                _byWoo.Remove(row.ExternalId);
                await _services.Items.RemoveAsync(ConnectorId, staleInv, cancellationToken);
                _logger.LogInformation("Connector {ConnectorId}: unlinked POSitive item {InvNo} from Woo {Woo} (replaced by a hand link)",
                    ConnectorId, staleInv, DescribeItem(row));
            }
            else
            {
                _logger.LogInformation("Connector {ConnectorId}: removed POSitive item {InvNo}'s link (replaced by a hand link)", ConnectorId, staleInv);
            }
        }
        return null;
    }

    /// <summary>PUT /products/{invNo}/link with the Woo id, then the item map row. False when POSitive refused the link.</summary>
    private async Task<bool> LinkAsync(int invNo, WooItem woo, string? sku, bool raiseOnFailure,
        CancellationToken cancellationToken, string? pushHash = null, string refusedKind = CatalogExceptionKinds.LinkConflict)
    {
        var wooRef = woo.Id.ToString(CultureInfo.InvariantCulture);
        var linked = await Pos.LinkProductAsync(invNo, wooRef, cancellationToken);
        if (!linked.Success)
        {
            Summary.LinkFailed++;
            if (raiseOnFailure)
                await RaiseAsync(refusedKind, wooRef, invNo,
                    $"Linking POSitive item {invNo} to Woo {Describe(woo)} was refused: {linked.Describe()}", cancellationToken);
            return false;
        }

        if (_byInv.TryGetValue(invNo, out var previous) && previous.ExternalId != woo.Id)
            _byWoo.Remove(previous.ExternalId);
        var row = new ConnectorItem(ConnectorId, invNo, woo.Id, woo.IsVariation ? woo.ParentId : 0, woo.Kind,
            string.IsNullOrWhiteSpace(sku) ? woo.Sku : sku.Trim(), pushHash, null);
        await _services.Items.UpsertAsync(row, cancellationToken);
        if (pushHash is not null)
            await _services.Items.SetPushHashAsync(ConnectorId, invNo, pushHash, cancellationToken);
        // Re-read nothing: a relink resets the hash (the store does that too), so the item is pushed.
        _byInv[invNo] = row with { LastPushHash = pushHash };
        _byWoo[woo.Id] = _byInv[invNo];

        foreach (var kind in new[] { CatalogExceptionKinds.UnmatchedPlatform, CatalogExceptionKinds.Ambiguous, CatalogExceptionKinds.LinkConflict,
                     CatalogExceptionKinds.ManualLinkRefused })
            _resolved.Add((kind, wooRef));
        foreach (var kind in new[] { CatalogExceptionKinds.UnmatchedPos, CatalogExceptionKinds.Ambiguous, CatalogExceptionKinds.LinkConflict })
            _resolved.Add((kind, CatalogExceptionKinds.PosRef(invNo)));
        return true;
    }

    private async Task SetHashAsync(ConnectorItem item, string hash, CancellationToken cancellationToken)
    {
        await _services.Items.SetPushHashAsync(ConnectorId, item.InvNo, hash, cancellationToken);
        _byInv[item.InvNo] = item with { LastPushHash = hash };
        _byWoo[item.ExternalId] = _byInv[item.InvNo];
    }

    // --- exceptions -------------------------------------------------------------

    private Task UnmatchedWooAsync(WooItem woo, string reason, bool raise, CancellationToken cancellationToken)
    {
        Summary.UnmatchedWoo++;
        // The review groups variations under their parent product and shows names: kept with the row.
        return raise
            ? RaiseAsync(CatalogExceptionKinds.UnmatchedPlatform, woo.Id.ToString(CultureInfo.InvariantCulture), null, reason, cancellationToken,
                ReviewSnapshot(new { wooId = woo.Id, parentId = woo.ParentId, type = woo.Kind, name = woo.Name, sku = woo.Sku, status = woo.Status }))
            : Task.CompletedTask;
    }

    /// <summary><paramref name="group"/>: why it is not created, once for many ("matrix", "no_sku") — the review explains it once.</summary>
    private Task UnmatchedPosAsync(PosProduct product, string reason, bool raise, CancellationToken cancellationToken, string? group = null)
    {
        Summary.UnmatchedPos++;
        return raise
            ? RaiseAsync(CatalogExceptionKinds.UnmatchedPos, CatalogExceptionKinds.PosRef(product.InvNo), product.InvNo, reason, cancellationToken,
                ReviewSnapshot(new { invNo = product.InvNo, sku = product.Sku, name = CatalogMapper.NameOf(product), group }))
            : Task.CompletedTask;
    }

    private static string ReviewSnapshot(object value) => JsonSerializer.Serialize(value, ReviewJson);

    private static readonly JsonSerializerOptions ReviewJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private Task AmbiguousAsync(string externalRef, int? invNo, string reason, bool raise, CancellationToken cancellationToken)
    {
        Summary.Ambiguous++;
        return raise ? RaiseAsync(CatalogExceptionKinds.Ambiguous, externalRef, invNo, reason, cancellationToken) : Task.CompletedTask;
    }

    private async Task RaiseAsync(string kind, string externalRef, int? invNo, string reason, CancellationToken cancellationToken,
        string? snapshot = null)
    {
        _logger.LogInformation("Catalog exception {Kind} {Ref}: {Reason}", kind, externalRef, reason);
        await _services.Exceptions.RaiseAsync(new ConnectorExceptionInput(
            ConnectorId, kind, externalRef, reason,
            PosRef: invNo?.ToString(CultureInfo.InvariantCulture), Snapshot: snapshot), cancellationToken);
        _resolved.Remove((kind, externalRef));
    }

    /// <summary>Closes the open catalog exceptions this run fixed (an item now linked, pushed or created).</summary>
    public async Task CloseResolvedAsync(CancellationToken cancellationToken)
    {
        if (_resolved.Count == 0 || await _services.Exceptions.CountOpenAsync(ConnectorId, cancellationToken) == 0)
            return;
        var open = await _services.Exceptions.ListAsync(ConnectorId, ConnectorExceptionStatuses.Open, 5000, cancellationToken);
        foreach (var row in open.Where(r => CatalogExceptionKinds.All.Contains(r.Kind) && _resolved.Contains((r.Kind, r.ExternalRef))))
            await _services.Exceptions.CloseAsync(row.ExceptionId, ConnectorExceptionStatuses.Resolved, "connector", cancellationToken);
    }

    /// <summary>What Woo holds of the item for orders POSitive does not have (ConnectorStockHold).</summary>
    private decimal Held(int invNo) => _held.GetValueOrDefault(invNo);

    private static string Ref(ConnectorItem item) => item.ExternalId.ToString(CultureInfo.InvariantCulture);

    private static string Describe(WooItem woo) =>
        woo.IsVariation ? $"variation #{woo.Id} of #{woo.ParentId}" : $"{woo.Kind} product #{woo.Id}";

    private static string DescribeItem(ConnectorItem item) =>
        item.ExternalType == CatalogItemTypes.Variation
            ? $"variation #{item.ExternalId} of #{item.ExternalParentId}"
            : $"product #{item.ExternalId}";

    private static string Join(IEnumerable<int> invNos) => string.Join(", ", invNos.Select(i => i.ToString(CultureInfo.InvariantCulture)));
}
