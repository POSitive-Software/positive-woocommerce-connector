using System.Runtime.CompilerServices;
using PA.Connectors.Core.Pos;

namespace PA.Connectors.Core.Catalog;

/// <summary>
/// Reading products from POSitive for the catalog push, over <see cref="IPosGateway.ListProductsAsync"/>.
/// <para>
/// Paging stops on a short page, never on <c>totalItems</c>: the list's total ignores
/// <c>modifiedSince</c> (design §7, G11), so it would page a change feed far past its end.
/// Items are de-duplicated by invNo (a row that changes while the feed is paged can shift
/// between blocks).
/// </para>
/// </summary>
public static class PosCatalog
{
    /// <summary>The API's page-size cap for /api/v2/products.</summary>
    public const int MaxPageSize = 200;

    private const int MaxPages = 100_000;

    /// <summary>
    /// Every product of <paramref name="query"/> (its Page/PageSize are ignored). A refused
    /// query (4xx: e.g. <c>modified_since_unsupported</c>) is permanent → a non-retryable job
    /// failure; transient faults throw from the gateway and the job retries.
    /// </summary>
    public static async IAsyncEnumerable<PosProduct> ReadAllAsync(
        IPosGateway pos, PosProductQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<int>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var result = await pos.ListProductsAsync(query with { Page = page, PageSize = MaxPageSize }, cancellationToken);
            if (!result.Success)
                throw new ConnectorJobException($"POSitive product list refused the catalog query: {result.Describe()}", retryable: false);

            var items = result.Value!.Items;
            foreach (var product in items)
            {
                if (seen.Add(product.InvNo))
                    yield return product;
            }

            var pageSize = result.Value.PageSize is > 0 and < MaxPageSize ? result.Value.PageSize : MaxPageSize;
            if (items.Count < pageSize)
                yield break;
        }
        throw new ConnectorJobException($"POSitive product list returned more than {MaxPages} pages; refusing to continue.", retryable: false);
    }

    public static async Task<List<PosProduct>> ListAllAsync(
        IPosGateway pos, PosProductQuery query, CancellationToken cancellationToken = default)
    {
        var all = new List<PosProduct>();
        await foreach (var product in ReadAllAsync(pos, query, cancellationToken))
            all.Add(product);
        return all;
    }

    /// <summary>One product by invNo (GET /products?invNo=), web flag or not; null when there is no such item.</summary>
    public static async Task<PosProduct?> GetAsync(IPosGateway pos, int invNo, CancellationToken cancellationToken = default)
    {
        var result = await pos.ListProductsAsync(new PosProductQuery { InvNo = invNo, PageSize = 1 }, cancellationToken);
        if (!result.Success)
            throw new ConnectorJobException($"POSitive product lookup for item {invNo} failed: {result.Describe()}", retryable: false);
        return result.Value!.Items.FirstOrDefault(p => p.InvNo == invNo);
    }
}
