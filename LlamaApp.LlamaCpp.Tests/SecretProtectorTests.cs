using LlamaApp.Common;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the DPAPI secret protector. The Hugging Face token grants the
/// user's private/gated-repo access, so the persisted form must be bound to
/// this user on this machine — and every failure path must read back as
/// "unset" rather than throw.
/// </summary>
public sealed class SecretProtectorTests
{
    [Fact]
    public void EmptySecret_StaysEmpty()
    {
        Assert.Equal("", SecretProtector.Protect(""));
        Assert.Equal("", SecretProtector.Unprotect(""));
        Assert.Equal("", SecretProtector.Unprotect(null));
    }

    [Fact]
    public void RoundTrips_ThroughDpapi()
    {
        var protectedForm = SecretProtector.Protect("hf_test_token");

        Assert.NotEqual("hf_test_token", protectedForm);
        Assert.Equal("hf_test_token", SecretProtector.Unprotect(protectedForm));
    }

    [Fact]
    public void ProtectedForm_IsNotPlaintext_AndDiffersEachTime()
    {
        var a = SecretProtector.Protect("hf_test_token");
        var b = SecretProtector.Protect("hf_test_token");

        // DPAPI output is non-deterministic, and neither form may leak the
        // plaintext into the settings file.
        Assert.NotEqual(a, b);
        Assert.DoesNotContain("hf_test_token", a);
    }

    [Theory]
    [InlineData("garbage not base64!!")]
    [InlineData("AAAA")]          // valid base64, not a DPAPI blob
    [InlineData("aGk=")]          // valid base64 ("hi"), not a DPAPI blob
    public void UndecryptableInput_ReadsBackAsUnset(string blob)
    {
        Assert.Equal("", SecretProtector.Unprotect(blob));
    }

    [Fact]
    public void UnicodeSecret_RoundTrips()
    {
        var secret = "hf_pässwörd-😀";

        Assert.Equal(secret, SecretProtector.Unprotect(SecretProtector.Protect(secret)));
    }
}
