using System.Diagnostics;
using PA.Connectors.Host.Ui;
using Xunit;
using Xunit.Abstractions;

namespace PA.Tests.Connectors.Host;

/// <summary>
/// Headless checks of the connector page's own logic: the served app.js runs in Node's <c>vm</c> with
/// a stub DOM (nothing is rendered or fetched), and the rail's pure functions are exercised — which
/// steps may be opened, which one opens, the ticks, and why Next is disabled. Skipped (passes with a
/// note) on a machine without <c>node</c> on PATH.
/// </summary>
public class ConnectorPageScriptTests(ITestOutputHelper output)
{
    private const string Harness = """
        'use strict';
        const vm = require('vm'), fs = require('fs'), assert = require('assert');
        const stub = () => ({ addEventListener() {}, setAttribute() {}, append() {}, remove() {}, focus() {}, querySelector: () => null,
          querySelectorAll: () => [], dataset: {}, style: {}, classList: { add() {}, remove() {}, contains: () => false }, textContent: '', innerHTML: '' });
        const document = { getElementById: stub, documentElement: { dataset: {} }, addEventListener() {}, querySelectorAll: () => [],
          querySelector: () => null, hidden: false, createElement: stub, body: stub() };
        const ctx = { document, window: { addEventListener() {} }, location: { hash: '', href: '' }, history: { replaceState() {} },
          fetch: () => new Promise(() => {}), localStorage: { getItem: () => null, setItem() {} }, setTimeout, clearTimeout, console, URL };
        vm.createContext(ctx);
        vm.runInContext(fs.readFileSync(process.argv[2], 'utf8')
          + ';globalThis.__t = { railModel, openStep, railHtml, nextBlock, storeSig, STEPS };', ctx);
        const t = ctx.__t;
        const conn = (states, extra) => Object.assign({ id: 'shop2', storeUrl: 'https://shop2.example.com', enabled: false,
          secrets: { wooConsumerKey: true, wooConsumerSecret: true, posApiKey: false },
          setup: { steps: ['store', 'settings', 'key', 'orders', 'enable'].map((key, i) => ({ number: i + 1, key, title: key, state: states[i], note: key + ' note' })),
            current: (['store', 'settings', 'key', 'orders', 'enable'].find((k, i) => states[i] !== 'done')) || null } }, extra || {});
        const reach = rail => rail.filter(r => r.reachable).map(r => r.key).join(',');

        // A new connector: only step 1 exists yet.
        let rail = t.railModel(null, 'store');
        assert.strictEqual(reach(rail), 'store');
        assert.strictEqual(rail[0].current, true);
        assert.strictEqual(t.openStep(null, 'enable'), 'store');
        assert.match(t.railHtml(rail), /data-step="key"[^>]*disabled/);

        // Mid-setup: done steps and the first to do may be opened; later ones wait.
        let c = conn(['done', 'todo', 'todo', 'todo', 'todo']);
        rail = t.railModel(c, 'settings');
        assert.strictEqual(reach(rail), 'store,settings');
        assert.strictEqual(t.openStep(c, 'enable'), 'settings');   // not reachable yet: the first to do opens
        assert.strictEqual(t.openStep(c, 'store'), 'store');       // a completed step: jump back to it
        let html = t.railHtml(rail);
        assert.match(html, /class="rs done" data-step="store"[^>]*><span class="rm" aria-hidden="true">✓/);
        assert.match(html, /class="rs todo on" data-step="settings" aria-current="step"/);

        // All done (an existing connector): every step opens; Settings by default; five ticks.
        c = conn(['done', 'done', 'done', 'done', 'done'], { enabled: true });
        rail = t.railModel(c, 'settings');
        assert.strictEqual(reach(rail), 'store,settings,key,orders,enable');
        assert.strictEqual(t.openStep(c, null), 'settings');
        assert.strictEqual(t.openStep(c, 'orders'), 'orders');
        assert.strictEqual((t.railHtml(rail).match(/>✓</g) || []).length, 5);

        // Needs attention: still every step opens, and the rail says so.
        c = conn(['done', 'done', 'attention', 'done', 'attention'], { enabled: true });
        rail = t.railModel(c, 'key');
        assert.strictEqual(reach(rail), 'store,settings,key,orders,enable');
        assert.strictEqual(t.openStep(c, null), 'key');
        assert.match(t.railHtml(rail), /class="rs attention on" data-step="key"[\s\S]*?needs attention/);

        // Next on step 1: id, address, keys, then a passing Test connection for exactly what is typed.
        const f = { id: 'shop2', storeUrl: 'https://shop2.example.com', consumerKey: 'ck_1', consumerSecret: 'cs_1' };
        assert.match(t.nextBlock('store', null, { ...f, id: '' }, null), /connector id/);
        assert.match(t.nextBlock('store', null, { ...f, storeUrl: 'shop' }, null), /store address/);
        assert.match(t.nextBlock('store', null, { ...f, consumerSecret: '' }, null), /together/);
        assert.match(t.nextBlock('store', null, f, null), /Test connection/);
        assert.strictEqual(t.nextBlock('store', null, f, t.storeSig(f)), '');
        assert.match(t.nextBlock('store', null, { ...f, consumerKey: 'ck_2' }, t.storeSig(f)), /Test connection/); // changed after the test
        // Saved with keys and unchanged: Next at once. Moved to another store: new keys first.
        c = conn(['done', 'todo', 'todo', 'todo', 'todo']);
        assert.strictEqual(t.nextBlock('store', c, { storeUrl: 'https://shop2.example.com/', consumerKey: '', consumerSecret: '' }, null), '');
        assert.match(t.nextBlock('store', c, { storeUrl: 'https://other.example.com', consumerKey: '', consumerSecret: '' }, null), /new store address/);

        // Settings, key, orders.
        assert.match(t.nextBlock('settings', c, { stockBuffer: '-1', pushIntervalSeconds: '60', orderPollMinutes: '5' }), /stock buffer/);
        assert.match(t.nextBlock('settings', c, { stockBuffer: '0', pushIntervalSeconds: '5', orderPollMinutes: '5' }), /15/);
        assert.strictEqual(t.nextBlock('settings', c, { stockBuffer: '0', pushIntervalSeconds: '60', orderPollMinutes: '5' }), '');
        assert.match(t.nextBlock('key', c, {}), /API key first/);
        assert.strictEqual(t.nextBlock('key', { ...c, secrets: { ...c.secrets, posApiKey: true } }, {}), '');
        assert.match(t.nextBlock('orders', c, { webhookBaseUrl: 'https://x.ngrok.app/hooks' }), /public address/);
        assert.match(t.nextBlock('orders', c, { webhookBaseUrl: '', orderMode: 'since', since: '' }), /date/);
        assert.strictEqual(t.nextBlock('orders', c, { webhookBaseUrl: 'https://x.ngrok.app', orderMode: 'now' }), '');
        console.log('rail ok');
        """;

    [Fact]
    public async Task TheRail_OpensTheRightSteps_TicksDoneOnes_AndSaysWhyNextIsDisabled()
    {
        var node = FindNode();
        if (node is null)
        {
            output.WriteLine("node is not on PATH: the headless page check is skipped.");
            return;
        }
        var dir = Path.Combine(Path.GetTempPath(), "pa-connector-js-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var app = Path.Combine(dir, "app.js");
            File.WriteAllBytes(app, ConnectorUiAssets.Read("app.js") ?? throw new InvalidOperationException("app.js is not embedded"));
            var harness = Path.Combine(dir, "rail-check.js");
            File.WriteAllText(harness, Harness);
            var start = new ProcessStartInfo(node, [harness, app])
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir,
            };
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var (said, complained) = (await stdout, await stderr);
            output.WriteLine(said + complained);
            Assert.True(process.ExitCode == 0, complained);
            Assert.Contains("rail ok", said);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string? FindNode()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "node.exe" } : ["node"];
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(d => names.Select(n => Path.Combine(d.Trim('"'), n)))
            .FirstOrDefault(File.Exists);
    }
}
