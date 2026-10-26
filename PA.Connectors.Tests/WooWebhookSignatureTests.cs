using System.Text;
using PA.Connectors.WooCommerce;
using Xunit;

namespace PA.Tests.Connectors;

public class WooWebhookSignatureTests
{
    // Recorded on the dev store (spike S0bc): order.updated delivery for order #59.
    private const string Secret = "spike-secret";
    private const string RecordedSignature = "u3P1bMNr+rOo1hrrbVK7z7HUBmro50GMSjljf7PMQnw=";

    private static byte[] Body => WooFixtures.Bytes("webhook-order-updated.body.json");

    [Fact]
    public void Fixture_IsByteExact()
    {
        // .gitattributes keeps the fixture -text; an EOL rewrite would change the size.
        Assert.Equal(3872, Body.Length);
        Assert.DoesNotContain((byte)'\r', Body);
    }

    [Fact]
    public void Compute_MatchesWhatWooSent()
    {
        Assert.Equal(RecordedSignature, WooWebhookSignature.Compute(Body, Secret));
    }

    [Fact]
    public void Verify_GoodSignature()
    {
        Assert.True(WooWebhookSignature.Verify(Body, Secret, RecordedSignature));
        Assert.True(WooWebhookSignature.Verify(Body, Secret, "  " + RecordedSignature + " "));
    }

    [Fact]
    public void Verify_WrongSecret_IsFalse()
    {
        Assert.False(WooWebhookSignature.Verify(Body, "not-the-secret", RecordedSignature));
    }

    [Fact]
    public void Verify_TamperedBody_IsFalse()
    {
        var tampered = (byte[])Body.Clone();
        tampered[^2] ^= 0x01;
        Assert.False(WooWebhookSignature.Verify(tampered, Secret, RecordedSignature));

        // Re-serialised JSON (same content, different bytes) must not verify either:
        // verification is on the RAW body, before any parsing.
        var reformatted = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Body) + "\n");
        Assert.False(WooWebhookSignature.Verify(reformatted, Secret, RecordedSignature));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64!!")]
    [InlineData("AAAA")] // valid base64, wrong length
    [InlineData("u3P1bMNr+rOo1hrrbVK7z7HUBmro50GMSjljf7PMQnw")] // truncated
    public void Verify_MissingOrMalformedHeader_IsFalse(string? header)
    {
        Assert.False(WooWebhookSignature.Verify(Body, Secret, header));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Verify_NoSecret_IsFalse(string? secret)
    {
        Assert.False(WooWebhookSignature.Verify(Body, secret, RecordedSignature));
    }

    [Fact]
    public void Ping_RecordedBody_IsRecognised()
    {
        var ping = WooFixtures.Bytes("webhook-ping.txt");
        Assert.Equal("webhook_id=2", Encoding.ASCII.GetString(ping));
        Assert.True(WooWebhookSignature.IsPing("application/x-www-form-urlencoded", ping));
        Assert.True(WooWebhookSignature.IsPing("application/x-www-form-urlencoded; charset=UTF-8", ping));

        // A ping carries no signature, so it never verifies — the receiver must
        // recognise it BEFORE verifying and answer 200 without enqueuing.
        Assert.False(WooWebhookSignature.Verify(ping, Secret, null));
    }

    [Theory]
    [InlineData("application/json", "webhook_id=2")]
    [InlineData("application/x-www-form-urlencoded", "webhook_id=2&x=1")]
    [InlineData("application/x-www-form-urlencoded", "webhook_id=")]
    [InlineData("application/x-www-form-urlencoded", "")]
    [InlineData(null, "webhook_id=2")]
    public void Ping_AnythingElse_IsNotAPing(string? contentType, string body)
    {
        Assert.False(WooWebhookSignature.IsPing(contentType, Encoding.ASCII.GetBytes(body)));
    }

    [Fact]
    public void Ping_OrderBody_IsNotAPing()
    {
        Assert.False(WooWebhookSignature.IsPing("application/json", Body));
    }
}
