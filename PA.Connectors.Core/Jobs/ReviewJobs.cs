using System.Text.Json;
using System.Text.Json.Serialization;

namespace PA.Connectors.Core.Jobs;

/// <summary>
/// Job kinds the S5 connector UI enqueues (it never runs catalog work inline: the worker
/// is the only consumer, so a UI action can never race a running push or match).
/// </summary>
public static class ReviewJobKinds
{
    /// <summary>The match review's "Run the match" (payload <see cref="CatalogMatchPayload"/>).</summary>
    public const string CatalogMatch = "catalog.match";

    /// <summary>"Create in Woo" for chosen POSitive items (payload <see cref="CatalogCreatePayload"/>).</summary>
    public const string CatalogCreate = "catalog.create";

    /// <summary>Link one platform item to one POSitive item by hand (payload <see cref="CatalogLinkPayload"/>).</summary>
    public const string CatalogLink = "catalog.link";

    public static bool Handles(string kind) => kind is CatalogMatch or CatalogCreate or CatalogLink;

    /// <summary>Cursor holding the JSON summary of the last review job (match / create / link) and when it ran.</summary>
    public const string LastRunCursor = "ui.catalog.lastRun";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T payload) => JsonSerializer.Serialize(payload, Json);

    /// <summary>Reads a payload case-insensitively; null when it is missing or not that shape.</summary>
    public static T? Read<T>(string? payload) where T : class
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(payload, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary><paramref name="CreateMissing"/> = also create the unmatched POSitive web items in the platform (status publishNewAs).</summary>
public sealed record CatalogMatchPayload(bool CreateMissing = true);

public sealed record CatalogCreatePayload(IReadOnlyList<int> InvNos);

/// <summary>
/// Link platform item <paramref name="ExternalId"/> (product or variation id) to POSitive item
/// <paramref name="InvNo"/>, then push its price/stock. <paramref name="RetryOrderId"/>: queue an
/// order.import of that platform order once the link is made (exceptions page: "Link item → Retry").
/// <paramref name="ReplaceExisting"/>: the owner confirmed "unlink the old one first" — whatever either
/// side is linked to now is unlinked (POSitive link + item map), then the new link is made.
/// </summary>
public sealed record CatalogLinkPayload(int InvNo, long ExternalId, long? RetryOrderId = null, bool ReplaceExisting = false);
