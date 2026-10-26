using System.Globalization;
using PA.Connectors.Core.Pos;

namespace PA.Connectors.Core.Orders;

/// <summary>
/// Authority rule 5 (design §4.4): an imported order must add up exactly as the
/// platform computed it. The transaction lines must sum to the platform's
/// <c>total − total_tax</c>, the header tax must equal <c>total_tax</c>, and the
/// lines' own tax must sum to it too — each within the platform's rounding
/// tolerance. Anything else becomes an exception and nothing is written; the
/// connector never adjusts a figure to make it fit.
/// </summary>
public static class OrderReconciliation
{
    /// <summary>Woo rounds every figure to the cent; ±0.01 absorbs that.</summary>
    public const decimal Tolerance = 0.01m;

    /// <summary>
    /// Per-line amounts are rounded line by line, the order once ("round at
    /// subtotal"), so a line sum may drift by up to half a cent per line.
    /// </summary>
    public const decimal PerLineTaxRounding = 0.005m;

    /// <summary>Checks <paramref name="transaction"/> against the platform's grand total (incl. tax) and total tax.</summary>
    public static OrderReconciliationResult Check(PosTransaction transaction, decimal platformTotal, decimal platformTotalTax)
    {
        var expectedNet = platformTotal - platformTotalTax;
        var lineTotal = transaction.Lines.Sum(l => l.LineTotal);
        var lineTax = transaction.Lines.Sum(l => l.TaxAmount);
        // Lines are rounded one by one (a tax-inclusive store derives each ex-tax line), the order
        // once, so both sums may drift by up to half a cent per line. The connector reads 6 places
        // (dp=6), which keeps the drift tiny; the per-line allowance is the backstop.
        var lineTolerance = LineTolerance(transaction.Lines.Count);

        var problems = new List<string>();
        if (Math.Abs(lineTotal - expectedNet) > lineTolerance)
            problems.Add($"lines add up to {M(lineTotal)} but the order total less tax is {M(expectedNet)} " +
                         $"({M(platformTotal)} − {M(platformTotalTax)})");
        if (Math.Abs(transaction.Total - expectedNet) > Tolerance)
            problems.Add($"header total {M(transaction.Total)} is not the order total less tax {M(expectedNet)}");
        if (Math.Abs(transaction.TaxAmount - platformTotalTax) > Tolerance)
            problems.Add($"header tax {M(transaction.TaxAmount)} is not the order tax {M(platformTotalTax)}");
        if (Math.Abs(lineTax - platformTotalTax) > lineTolerance)
            problems.Add($"line taxes add up to {M(lineTax)} but the order tax is {M(platformTotalTax)}");

        return new OrderReconciliationResult(problems.Count == 0, problems, lineTotal, lineTax, expectedNet, platformTotalTax);
    }

    /// <summary>max(0.01, 0.005 × lines): what line-by-line rounding can add up to.</summary>
    public static decimal LineTolerance(int lineCount) => Math.Max(Tolerance, PerLineTaxRounding * lineCount);

    private static string M(decimal value) => value.ToString("0.00##", CultureInfo.InvariantCulture);
}

/// <summary>Outcome of <see cref="OrderReconciliation.Check"/>; <see cref="Problems"/> is empty when it balances.</summary>
public sealed record OrderReconciliationResult(
    bool Balanced,
    IReadOnlyList<string> Problems,
    decimal LineTotal,
    decimal LineTax,
    decimal ExpectedNet,
    decimal ExpectedTax)
{
    public string Describe() => Balanced ? "balanced" : string.Join("; ", Problems) + ".";
}
