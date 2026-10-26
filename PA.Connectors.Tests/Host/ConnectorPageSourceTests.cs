using System.Text.RegularExpressions;
using PA.Connectors.Host.Ui;
using Xunit;

namespace PA.Tests.Connectors.Host;

/// <summary>
/// Source checks on the connector pages (no JS test runner, like StaticPageEscapingTests for
/// wwwroot): the escaper handles both quote characters, and nothing inline would break the
/// page's CSP (<c>script-src 'self'</c>) — no inline script blocks or on* handler attributes.
/// The embedded copies are the ones served.
/// </summary>
public class ConnectorPageSourceTests
{
    private static string Embedded(string file) =>
        System.Text.Encoding.UTF8.GetString(ConnectorUiAssets.Read(file) ?? throw new InvalidOperationException($"{file} is not embedded"));

    [Fact]
    public void Escaper_EscapesAmpersandAnglesAndBothQuotes()
    {
        var m = Regex.Match(Embedded("app.js"), @"const esc = s => String\(s\)(?<body>(?:\s*\.replace\([^)]*\))+)\s*;");
        Assert.True(m.Success, "no `const esc = s => String(s).replace(…)…;` in app.js");
        var body = m.Groups["body"].Value;
        Assert.StartsWith(".replace(/&/g", body.TrimStart());
        foreach (var (pattern, entity) in new[] { ("<", "&lt;"), (">", "&gt;"), ("\"", "&quot;"), ("'", "&#39;") })
            Assert.Contains($"replace(/{pattern}/g, '{entity}')", body);
    }

    [Fact]
    public void ThePage_HasNoInlineScriptOrHandlers_SoTheCspHolds()
    {
        var html = Embedded("index.html");
        var js = Embedded("app.js");

        Assert.Matches(@"<script src=""/connectors/app\.js""></script>", html);
        Assert.Single(Regex.Matches(html, "<script"));
        Assert.DoesNotMatch(@"\son[a-z]+\s*=", html);
        Assert.DoesNotMatch(@"\son(click|change|input|load|error|mouseover)\s*=", js);
        Assert.DoesNotContain("eval(", js);
    }

    // F5: no browser-native dialogs; confirmations are the page's own (a <dialog> built with DOM calls).
    [Fact]
    public void ThePage_UsesNoNativeConfirmOrAlert()
    {
        var code = Regex.Replace(Embedded("app.js"), @"^\s*//.*$", "", RegexOptions.Multiline);

        Assert.DoesNotMatch(@"(?<![\w.])(window\.)?(confirm|alert|prompt)\s*\(", code);
        Assert.Contains("function ask(", code);
        Assert.Contains("showModal()", code);
    }

    // F8: polling pauses while the tab is hidden, backs off after 10 idle minutes, and one poller per key
    // (a re-render replaces its poller — the old setInterval per render piled them up).
    [Fact]
    public void Polling_PausesWhenHidden_BacksOffWhenIdle_AndNeverStacks()
    {
        var js = Embedded("app.js");

        Assert.Contains("visibilitychange", js);
        Assert.Contains("document.hidden", js);
        Assert.Matches(@"IDLE_AFTER = 10 \* 60 \* 1000, IDLE_EVERY = 30000", js);
        Assert.DoesNotContain("setInterval(", js);
        foreach (Match call in Regex.Matches(js, @"(?<!function )\bevery\((?<arg>[^,]+),"))
            Assert.StartsWith("'", call.Groups["arg"].Value.Trim()); // every('key', …)
    }
}
