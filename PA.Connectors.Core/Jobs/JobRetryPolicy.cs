namespace PA.Connectors.Core.Jobs;

/// <summary>Decision for one failed attempt: retry after <see cref="Delay"/>, or give up.</summary>
public readonly record struct JobFailureDecision(bool Retry, TimeSpan Delay);

/// <summary>
/// Exponential backoff with a ceiling and a maximum number of attempts. Pure,
/// so the SQLite store and the tests share one rule. Attempts count CLAIMS (the
/// store increments on claim), so a job that crashes the process mid-run also
/// burns an attempt and a poison job cannot loop forever.
/// </summary>
public sealed record JobRetryPolicy(TimeSpan BaseDelay, TimeSpan MaxDelay, int MaxAttempts)
{
    /// <summary>30 s, 1 m, 2 m, 4 m … capped at 1 h; 8 attempts (≈ 2 h of retries) before an exception is raised.</summary>
    public static JobRetryPolicy Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 8);

    /// <summary>
    /// order.import rides out a long outage instead of giving up after ≈ 2 h: the same backoff
    /// (30 s doubling, capped at 1 h) but 31 attempts, ≈ 24 h of retries (63.5 min to reach the cap,
    /// then 23 hourly tries). A POSitive or Woo outage (PosApiException, 5xx, timeouts, connection
    /// failures) over a weekend therefore never exhausts an order; and if one still does, the next
    /// order.poll re-queues it (<see cref="Stores.IConnectorJobStore.ListStrandedAsync"/>).
    /// </summary>
    public static JobRetryPolicy OrderImport { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 31);

    /// <summary>The engine's policy for a job kind: <see cref="OrderImport"/> for order.import, else <see cref="Default"/>.</summary>
    public static JobRetryPolicy For(string kind) => kind == ConnectorJobKinds.OrderImport ? OrderImport : Default;

    /// <summary>Total wait before the job is dead-lettered (the sum of every retry delay).</summary>
    public TimeSpan TotalRetryTime()
    {
        var total = TimeSpan.Zero;
        for (var attempt = 1; attempt < MaxAttempts; attempt++)
            total += DelayFor(attempt);
        return total;
    }

    /// <param name="attempts">Attempts made so far, including the one that just failed (≥ 1).</param>
    /// <param name="retryable">False = permanent failure (bad data, not a transient fault): give up now.</param>
    public JobFailureDecision Decide(int attempts, bool retryable = true)
    {
        if (!retryable || attempts >= MaxAttempts)
            return new JobFailureDecision(false, TimeSpan.Zero);
        return new JobFailureDecision(true, DelayFor(attempts));
    }

    /// <summary>Delay before the next try after <paramref name="attempts"/> failures: Base × 2^(attempts−1), capped.</summary>
    public TimeSpan DelayFor(int attempts)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 30);
        var ticks = BaseDelay.Ticks * Math.Pow(2, exponent);
        return ticks >= MaxDelay.Ticks ? MaxDelay : TimeSpan.FromTicks((long)ticks);
    }
}
