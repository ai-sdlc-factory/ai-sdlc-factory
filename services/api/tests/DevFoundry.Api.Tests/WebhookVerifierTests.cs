using System.Security.Cryptography;
using System.Text;
using DevFoundry.Api;

namespace DevFoundry.Api.Tests;

public sealed class WebhookVerifierTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"action":"opened"}""");

    private static string Sign(string secret, byte[] body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    [Fact]
    public void Correct_signature_is_accepted_in_either_hex_case()
    {
        var signature = Sign(Secret, Body);
        Assert.True(WebhookVerifier.IsValidSignature(Secret, Body, signature));
        Assert.True(WebhookVerifier.IsValidSignature(Secret, Body, signature.ToUpperInvariant()));
    }

    [Fact]
    public void Wrong_secret_or_tampered_body_is_rejected()
    {
        Assert.False(WebhookVerifier.IsValidSignature(Secret, Body, Sign("another-secret-another-secret-123", Body)));
        Assert.False(WebhookVerifier.IsValidSignature(Secret, [.. Body, 0x20], Sign(Secret, Body)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zz00000000000000000000000000000000000000000000000000000000000000")]
    public void Malformed_signatures_are_not_well_formed(string signature) =>
        Assert.False(WebhookVerifier.IsWellFormedSignature(signature));

    [Fact]
    public void Sha256_hex_digest_is_well_formed() =>
        Assert.True(WebhookVerifier.IsWellFormedSignature(Sign(Secret, Body)));

    [Fact]
    public async Task Body_within_the_limit_is_returned_intact()
    {
        using var stream = new MemoryStream(Body);
        var result = await WebhookVerifier.ReadBoundedAsync(stream, Body.Length, CancellationToken.None);
        Assert.Equal(Body, result);
    }

    [Fact]
    public async Task Body_over_the_limit_returns_null()
    {
        using var stream = new MemoryStream(Body);
        var result = await WebhookVerifier.ReadBoundedAsync(stream, Body.Length - 1, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task Limit_is_enforced_on_streams_with_unknown_length()
    {
        using var stream = new NonSeekableStream(new byte[20_000]);
        Assert.Null(await WebhookVerifier.ReadBoundedAsync(stream, 10_000, CancellationToken.None));
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
