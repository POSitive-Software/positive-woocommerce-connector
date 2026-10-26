using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PA.Connectors.Core.Pos;
using PA.Connectors.WooCommerce.Models;

namespace PA.Connectors.WooCommerce.Orders;

/// <summary>How one Woo product line resolved to a POSitive item (or why it did not).</summary>
public sealed record WooLineResolution(long LineId, long WooItemId, string? Sku, string? Name, int? InvNo, string? Problem)
{
    public bool Resolved => InvNo is not null;
}

/// <summary>
/// Pure Woo order → POSitive mapping (design §4.3–4.4). No I/O: the import
/// resolves the items first and hands the results in. Every width is the
/// public API's own (docs/openapi/v2.json); text is cut, never rejected.
/// Money stays decimal end to end.
/// </summary>
public static class WooOrderMapper
{
    public const string TransactionPrefix = "woo-";
    public const string RegisteredCustomerPrefix = "woo-c";
    public const string GuestCustomerPrefix = "woo-g";
    public const string FreightSku = "FREIGHT";
    private const int IdWidth = 100;
    private const int NoteWidth = 1000;

    /// <summary>localTransactionId: <c>woo-&lt;order id&gt;</c> (a replay is 409 transaction_exists).</summary>
    public static string LocalTransactionId(long orderId) =>
        TransactionPrefix + orderId.ToString(CultureInfo.InvariantCulture);

    /// <summary>localPaymentId: <c>woo-&lt;order id&gt;-pay</c> (a replay is 409 payment_exists, API item A1).</summary>
    public static string LocalPaymentId(long orderId) => LocalTransactionId(orderId) + "-pay";

    /// <summary>
    /// <c>woo-c&lt;customer_id&gt;</c> for a registered customer; <c>woo-g&lt;lowercased trimmed
    /// billing email&gt;</c> for a guest, so repeat guests share one POSitive customer.
    /// A guest key over the API's 100 characters becomes <c>woo-gh</c> + a SHA-256 prefix.
    /// Null = a guest with no email (cannot be keyed).
    /// </summary>
    public static string? LocalCustomerId(WooOrder order)
    {
        if (!order.IsGuest)
            return RegisteredCustomerPrefix + order.CustomerId.ToString(CultureInfo.InvariantCulture);
        var email = order.Billing.Email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email))
            return null;
        var key = GuestCustomerPrefix + email;
        if (key.Length <= IdWidth)
            return key;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email))).ToLowerInvariant();
        return GuestCustomerPrefix + "h" + hash[..40];
    }

    /// <summary>The customer to create, from the order's billing details (CustomerWriteDto widths).</summary>
    public static PosCustomer Customer(WooOrder order, string localCustomerId)
    {
        var billing = order.Billing;
        var company = Cut(billing.Company, 60);
        var first = Cut(billing.FirstName, 30);
        var last = Cut(billing.LastName, 30);
        if (company is null && first is null && last is null)
            last = Cut(billing.Email?.Split('@')[0], 30) ?? "Woo customer";
        // Without a company the display name is "last, first" and must fit 60.
        if (company is null && first is not null && last is not null && last.Length + 2 + first.Length > 60)
            first = first[..Math.Max(1, 60 - 2 - last.Length)];

        var business = company is not null;
        var phone = Cut(billing.Phone, 20);
        var email = Clean(billing.Email);
        return new PosCustomer
        {
            LocalCustomerId = localCustomerId,
            CustomerType = business ? "B" : "P",
            Company = company,
            FirstName = first,
            LastName = last,
            Email = email is { Length: <= 200 } ? email : null,
            // Business customers file their first number as work; everyone else as home.
            WorkPhone = business ? phone : null,
            HomePhone = business ? null : phone,
        };
    }

    /// <summary>
    /// The PATCH for an existing customer: only the contact fields whose value
    /// on the order is non-blank AND differs from what POSitive holds (null when
    /// nothing changed). Phones compare by digits, area code included, because
    /// POSitive files the area code separately. The customer type is never changed.
    /// </summary>
    public static PosCustomer? CustomerChanges(PosCustomer wanted, PosCustomer current)
    {
        var changes = new PosCustomer
        {
            Email = Differs(wanted.Email, current.Email, StringComparison.OrdinalIgnoreCase) ? wanted.Email : null,
            Company = Differs(wanted.Company, current.Company, StringComparison.Ordinal) ? wanted.Company : null,
            FirstName = Differs(wanted.FirstName, current.FirstName, StringComparison.Ordinal) ? wanted.FirstName : null,
            LastName = Differs(wanted.LastName, current.LastName, StringComparison.Ordinal) ? wanted.LastName : null,
        };
        var wantedPhone = wanted.HomePhone ?? wanted.WorkPhone;
        if (!string.IsNullOrWhiteSpace(wantedPhone))
        {
            var held = new[]
            {
                Digits(current.HomePhoneAreaCode + current.HomePhone),
                Digits(current.WorkPhoneAreaCode + current.WorkPhone),
                Digits(current.CellPhone),
            };
            if (!held.Contains(Digits(wantedPhone)))
                changes = changes with { HomePhone = wanted.HomePhone, WorkPhone = wanted.WorkPhone };
        }
        return changes == new PosCustomer() ? null : changes;
    }

    /// <summary>The billing address write (address fields only, so it never renames the customer). Null when the order has none.</summary>
    public static PosAddress? BillingAddress(WooOrder order, string localCustomerId)
    {
        var b = order.Billing;
        if (Clean(b.Address1) is null && Clean(b.City) is null && Clean(b.Postcode) is null)
            return null;
        return new PosAddress
        {
            LocalCustomerId = localCustomerId,
            Type = "Billing",
            Address1 = Cut(b.Address1, 35),
            Address2 = Cut(b.Address2, 35),
            City = Cut(b.City, 30),
            Region = Cut(b.State, 30),
            Postcode = Cut(b.Postcode, 20),
            CountryCode = Cut(b.Country, 26),
        };
    }

    /// <summary>
    /// The ship-to, sent inline on the transaction (API item A4). No
    /// localAddressId: the API then reuses an identical address the customer
    /// already has and adds a new one only when it differs, so a repeat
    /// customer does not collect one ship-to per order. Null = the order has no
    /// shipping address (virtual goods): POSitive uses the customer's default.
    /// </summary>
    public static PosShipTo? ShipTo(WooOrder order)
    {
        var s = order.Shipping;
        if (Clean(s.Address1) is null && Clean(s.City) is null && Clean(s.Postcode) is null)
            return null;
        var contact = Clean($"{s.FirstName} {s.LastName}");
        return new PosShipTo
        {
            Company = Cut(s.Company, 60),
            Contact = Cut(contact, 35),
            Address1 = Cut(s.Address1, 35),
            Address2 = Cut(s.Address2, 35),
            City = Cut(s.City, 30),
            Region = Cut(s.State, 30),
            Postcode = Cut(s.Postcode, 20),
            CountryCode = Cut(s.Country, 26),
            Phone = Cut(Clean(s.Phone) ?? order.Billing.Phone, 20),
            Email = Clean(order.Billing.Email) is { Length: <= 200 } email ? email : null,
        };
    }

    /// <summary>
    /// The transaction. <paramref name="resolved"/> holds one resolution per
    /// <see cref="WooOrder.LineItems"/> entry (all resolved — the import checks
    /// first). <paramref name="freightInvNo"/> = the store's FREIGHT item, or null
    /// to book shipping as a non-inventory line (design §7.1).
    /// <para>
    /// Woo's REST line, shipping and fee <c>total</c>s are ex tax whatever
    /// <c>prices_include_tax</c> says (Woo stores line totals ex tax and the tax
    /// beside them), so they are used as they are for both settings; the
    /// reconciliation proves it per order.
    /// </para>
    /// </summary>
    public static PosTransaction Transaction(
        WooOrder order, string localCustomerId, IReadOnlyList<WooLineResolution> resolved, int? freightInvNo,
        TimeZoneInfo storeLocalZone)
    {
        var lines = new List<PosTransactionLine>();
        for (var i = 0; i < order.LineItems.Count; i++)
        {
            var item = order.LineItems[i];
            var invNo = resolved[i].InvNo ?? throw new InvalidOperationException($"Line {item.Id} is not resolved.");
            lines.Add(new PosTransactionLine
            {
                LocalLineId = "woo-li" + item.Id.ToString(CultureInfo.InvariantCulture),
                ProductId = invNo,
                // Blank = POSitive's own item description.
                Quantity = item.Quantity,
                Price = UnitPrice(item.Total, item.Quantity),
                LineTotal = item.Total,
                TaxAmount = item.TotalTax,
            });
        }

        foreach (var shipping in order.ShippingLines)
        {
            if (shipping.Total == 0 && shipping.TotalTax == 0)
                continue; // free shipping: nothing to book
            var title = Clean(shipping.MethodTitle) ?? Clean(shipping.MethodId) ?? "Shipping";
            lines.Add(new PosTransactionLine
            {
                LocalLineId = "woo-sl" + shipping.Id.ToString(CultureInfo.InvariantCulture),
                ProductId = freightInvNo,
                Description = Cut(freightInvNo is null ? "Shipping - " + title : title, 40),
                Quantity = 1,
                Price = shipping.Total,
                LineTotal = shipping.Total,
                TaxAmount = shipping.TotalTax,
            });
        }

        foreach (var fee in order.FeeLines)
        {
            lines.Add(new PosTransactionLine
            {
                LocalLineId = "woo-fl" + fee.Id.ToString(CultureInfo.InvariantCulture),
                Description = Cut("Fee - " + (Clean(fee.Name) ?? "fee"), 40),
                Quantity = 1,
                Price = fee.Total, // may be negative (a discount fee)
                LineTotal = fee.Total,
                TaxAmount = fee.TotalTax,
            });
        }

        var pickup = order.ShippingLines.Count > 0
                     && order.ShippingLines.All(s => s.MethodId?.StartsWith("local_pickup", StringComparison.OrdinalIgnoreCase) == true);
        return new PosTransaction
        {
            LocalTransactionId = LocalTransactionId(order.Id),
            LocalCustomerId = localCustomerId,
            ShippingAddress = pickup ? null : ShipTo(order),
            TransactionType = "N",
            DeliveryMethod = pickup ? "C" : null,
            CreatedAt = StoreLocal(order.DateCreatedUtc, storeLocalZone),
            PurchaseOrderNumber = Cut(order.Number, 30),
            TaxAmount = order.TotalTax,
            Total = order.Total - order.TotalTax,
            Note = Note(order),
            Lines = lines,
        };
    }

    /// <summary>
    /// The transaction as sent: every amount to the cent, adding up exactly. Header total =
    /// round(total − total_tax), header tax = round(total_tax); each line's total and tax is rounded,
    /// and the cents that line-by-line rounding gained or lost are moved onto the lines whose
    /// rounding moved them most (largest remainder), so Σ line totals = header total and Σ line tax =
    /// header tax. Called only after <c>OrderReconciliation.Check</c> passed on the
    /// unrounded (dp=6) figures, so at most half a cent per line is ever moved.
    /// </summary>
    public static PosTransaction ToCents(PosTransaction transaction)
    {
        var net = Cents(transaction.Total);
        var tax = Cents(transaction.TaxAmount);
        var totals = Allocate(transaction.Lines.Select(l => l.LineTotal).ToList(), net);
        var taxes = Allocate(transaction.Lines.Select(l => l.TaxAmount).ToList(), tax);
        var lines = transaction.Lines
            .Select((l, i) => l with { LineTotal = totals[i], TaxAmount = taxes[i], Price = UnitPrice(totals[i], l.Quantity) })
            .ToList();
        return transaction with { Total = net, TaxAmount = tax, Lines = lines };
    }

    private static decimal Cents(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal[] Allocate(IReadOnlyList<decimal> raw, decimal target)
    {
        var rounded = raw.Select(Cents).ToArray();
        if (rounded.Length == 0)
            return rounded;
        var residual = (int)Math.Round((target - rounded.Sum()) * 100m, MidpointRounding.AwayFromZero);
        if (residual == 0)
            return rounded;
        var sign = Math.Sign(residual);
        var order = Enumerable.Range(0, raw.Count)
            .OrderByDescending(i => (raw[i] - rounded[i]) * sign)
            .ThenBy(i => i)
            .ToList();
        for (var k = 0; k < Math.Abs(residual); k++)
            rounded[order[k % order.Count]] += 0.01m * sign;
        return rounded;
    }

    /// <summary>"Woo order #N. Payment: …. Coupons: …. Customer note: …" (cut to 1000).</summary>
    public static string Note(WooOrder order)
    {
        var parts = new List<string> { $"WooCommerce order #{Clean(order.Number) ?? order.Id.ToString(CultureInfo.InvariantCulture)}" };
        if (Clean(order.PaymentMethodTitle) is { } payment)
            parts.Add("Payment: " + payment);
        var coupons = order.CouponLines.Select(c => Clean(c.Code)).Where(c => c is not null).ToList();
        if (coupons.Count > 0)
            parts.Add("Coupons: " + string.Join(", ", coupons));
        if (Clean(order.CustomerNote) is { } note)
            parts.Add("Customer note: " + note);
        var text = string.Join(". ", parts);
        return text.Length <= NoteWidth ? text : text[..NoteWidth];
    }

    /// <summary>Unit price after coupons, ex tax: total / qty, 4 places (the API keeps 4).</summary>
    public static decimal UnitPrice(decimal total, decimal quantity) =>
        quantity == 0 ? total : Math.Round(total / quantity, 4, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The API takes createdAt as store-local wall time with no offset; Woo's
    /// *_gmt is UTC. The host runs beside PA.Server, so its zone is the store's.
    /// </summary>
    public static DateTime? StoreLocal(DateTime? utc, TimeZoneInfo zone) =>
        utc is null
            ? null
            : DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc), zone),
                DateTimeKind.Unspecified);

    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string? Cut(string? value, int width)
    {
        var clean = Clean(value);
        return clean is null || clean.Length <= width ? clean : clean[..width].TrimEnd();
    }

    private static bool Differs(string? wanted, string? current, StringComparison comparison) =>
        wanted is not null && !string.Equals(wanted.Trim(), current?.Trim() ?? "", comparison);

    private static string Digits(string? value) => new((value ?? "").Where(char.IsDigit).ToArray());
}
