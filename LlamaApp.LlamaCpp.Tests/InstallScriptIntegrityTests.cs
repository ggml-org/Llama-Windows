using System.Text;
using LlamaApp.Common;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests the installer provenance policy. The app executes the downloaded
/// <c>install.ps1</c> with <c>-ExecutionPolicy Bypass</c>, so every check here
/// is a barrier between a hostile/compromised endpoint and arbitrary code
/// execution: exact URL, no redirects, bounded size, and a hash pin for
/// unattended installs.
/// </summary>
public class InstallScriptIntegrityTests
{
    private static readonly Uri Good = new(InstallScriptIntegrity.ExpectedScriptUrl);

    // ---- ValidateResponse ----

    [Fact]
    public void Accepts_Direct_200_From_Expected_Url()
    {
        Assert.Equal(
            InstallScriptVerdict.Ok,
            InstallScriptIntegrity.ValidateResponse(200, Good, 5636));
    }

    [Fact]
    public void Accepts_Unknown_ContentLength()
    {
        // Chunked responses carry no Content-Length; the copy loop still bounds them.
        Assert.Equal(
            InstallScriptVerdict.Ok,
            InstallScriptIntegrity.ValidateResponse(200, Good, null));
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public void Refuses_Redirects_Instead_Of_Following(int status)
    {
        Assert.Equal(
            InstallScriptVerdict.Redirected,
            InstallScriptIntegrity.ValidateResponse(status, Good, null));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    public void Refuses_Non200(int status)
    {
        Assert.Equal(
            InstallScriptVerdict.HttpError,
            InstallScriptIntegrity.ValidateResponse(status, Good, null));
    }

    [Theory]
    [InlineData("https://evil.example/install.ps1")]
    [InlineData("https://llama.app.evil.example/install.ps1")]
    [InlineData("http://llama.app/install.ps1")]
    [InlineData("https://llama.app:8443/install.ps1")]
    [InlineData("https://llama.app/other.ps1")]
    [InlineData("https://llama.app/install.ps1/extra")]
    [InlineData("https://llama.app")]
    public void Refuses_Unexpected_Uri(string uri)
    {
        Assert.Equal(
            InstallScriptVerdict.WrongUri,
            InstallScriptIntegrity.ValidateResponse(200, new Uri(uri), null));
    }

    [Fact]
    public void Refuses_Null_Final_Uri()
    {
        Assert.Equal(
            InstallScriptVerdict.WrongUri,
            InstallScriptIntegrity.ValidateResponse(200, null, null));
    }

    [Fact]
    public void Refuses_Declared_Size_Over_Limit()
    {
        Assert.Equal(
            InstallScriptVerdict.TooLarge,
            InstallScriptIntegrity.ValidateResponse(200, Good, InstallScriptIntegrity.MaxBytes + 1));
        Assert.Equal(
            InstallScriptVerdict.TooLarge,
            InstallScriptIntegrity.ValidateResponse(200, Good, 0));
    }

    // ---- IsExpectedUri ----

    [Theory]
    [InlineData("https://llama.app/install.ps1", true)]
    [InlineData("HTTPS://LLAMA.APP/install.ps1", true)]
    [InlineData("https://llama.app:443/install.ps1", true)]
    [InlineData("https://llama.app/Install.ps1", false)]
    [InlineData("https://cdn.llama.app/install.ps1", false)]
    [InlineData("https://llama.app/install.ps1?x=1", false)]
    public void IsExpectedUri_Checks(string uri, bool expected)
    {
        Assert.Equal(expected, InstallScriptIntegrity.IsExpectedUri(new Uri(uri)));
    }

    // ---- ValidateHash ----

    [Fact]
    public void Unpinned_When_No_Pin_Configured()
    {
        Assert.Equal(
            InstallScriptVerdict.Unpinned,
            InstallScriptIntegrity.ValidateHash("abc123", pinned: null));
        Assert.Equal(
            InstallScriptVerdict.Unpinned,
            InstallScriptIntegrity.ValidateHash("abc123", pinned: "   "));
    }

    [Fact]
    public void Matches_Pin_CaseInsensitively()
    {
        Assert.Equal(
            InstallScriptVerdict.Ok,
            InstallScriptIntegrity.ValidateHash("ABCDEF", pinned: "abcdef"));
    }

    [Fact]
    public void Mismatched_Pin_Is_Fatal()
    {
        Assert.Equal(
            InstallScriptVerdict.HashMismatch,
            InstallScriptIntegrity.ValidateHash("abcdef", pinned: "123456"));
        Assert.Equal(
            InstallScriptVerdict.HashMismatch,
            InstallScriptIntegrity.ValidateHash(null, pinned: "123456"));
    }

    // ---- Sha256Hex ----

    [Fact]
    public void Sha256Hex_Matches_Known_Vector()
    {
        // SHA-256("abc")
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            InstallScriptIntegrity.Sha256Hex(Encoding.ASCII.GetBytes("abc")));
    }

    // ---- ReadBoundedAsync ----

    [Fact]
    public async Task ReadBounded_Returns_Under_Limit()
    {
        var data = Encoding.ASCII.GetBytes("hello");
        using var stream = new MemoryStream(data);
        Assert.Equal(data, await InstallScriptIntegrity.ReadBoundedAsync(stream, maxBytes: 1024));
    }

    [Fact]
    public async Task ReadBounded_Throws_Over_Limit()
    {
        var data = new byte[2048];
        using var stream = new MemoryStream(data);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => InstallScriptIntegrity.ReadBoundedAsync(stream, maxBytes: 1024));
    }
}
