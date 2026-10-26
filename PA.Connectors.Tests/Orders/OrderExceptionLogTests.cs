using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PA.Connectors.Core.Orders;
using Xunit;

namespace PA.Tests.Connectors.Orders;

/// <summary>
/// Order exceptions reach the connector log like catalog ones: raised, raised again
/// with its occurrence, resolved — naming the kind and the Woo order id, never the customer.
/// </summary>
public sealed class OrderExceptionLogTests : IAsyncLifetime
{
    private const int CapInvNo = 1001;
    private readonly ListLoggerFactory _log = new();
    private OrderImportHarness _h = null!;

    public async Task InitializeAsync()
    {
        _h = await OrderImportHarness.CreateAsync();
        _h.LoggerFactory = _log;
        _h.Rebuild();
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private List<string> OrderExceptionLines() => _log.Lines.Where(l => l.StartsWith("Order exception ", StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task UnmatchedOrder_LogsRaised_ThenRaisedAgain_ThenResolved_WithoutTheCustomer()
    {
        _h.Woo.AddOrder("order-guest-paid.json"); // Cap (Woo #16) not linked yet

        await _h.ImportAsync(58);
        await _h.ImportAsync(58);
        await _h.LinkAsync(16, CapInvNo);
        await _h.ImportAsync(58);

        var lines = OrderExceptionLines();
        Assert.Equal(3, lines.Count);
        Assert.StartsWith($"Order exception {OrderExceptionKinds.UnmatchedProduct} 58: raised (exception ", lines[0], StringComparison.Ordinal);
        Assert.StartsWith($"Order exception {OrderExceptionKinds.UnmatchedProduct} 58: raised again, occurrence 2 (exception ", lines[1], StringComparison.Ordinal);
        Assert.StartsWith($"Order exception {OrderExceptionKinds.UnmatchedProduct} 58: resolved, imported on retry (exception ", lines[2], StringComparison.Ordinal);
        Assert.Contains(", invoice ", lines[2]);

        // The guest's name and email never reach the log (the guest key is the billing email).
        foreach (var line in _log.Lines)
        {
            Assert.DoesNotContain("guest@example.com", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Gina", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ChangedAfterImport_IsLoggedWhenRaised()
    {
        var order = _h.Woo.AddOrder("order-guest-paid.json");
        await _h.LinkAsync(16, CapInvNo);
        await _h.ImportAsync(58);

        order["status"] = "cancelled";
        await _h.ImportAsync(58);

        var line = Assert.Single(OrderExceptionLines());
        Assert.StartsWith($"Order exception {OrderExceptionKinds.ChangedAfterImport} 58: raised (exception ", line, StringComparison.Ordinal);
        Assert.Contains(", invoice ", line);
    }

    private sealed class ListLoggerFactory : ILoggerFactory, ILogger
    {
        public ConcurrentQueue<string> Queue { get; } = new();
        public List<string> Lines => Queue.ToList();
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Queue.Enqueue(formatter(state, exception));
    }
}
