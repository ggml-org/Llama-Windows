using LlamaApp.Common;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the overlay WebView2's navigation policy. The overlay is a
/// chromeless window with no address bar, so only the local llama server's own
/// origin may render inside it — anything else would display with the app's
/// visual authority.
/// </summary>
public sealed class WebOriginPolicyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:9931/")]
    [InlineData("http://127.0.0.1:9931")]
    [InlineData("http://127.0.0.1:9931/?model=a%2Fb")]
    [InlineData("http://127.0.0.1:9931/conversation")]
    public void ServerOrigin_IsAllowed(string uri)
    {
        Assert.True(WebOriginPolicy.IsServerOrigin(new Uri(uri), "127.0.0.1", 9931));
    }

    [Theory]
    [InlineData("http://evil.example:9931/")]            // wrong host
    [InlineData("http://127.0.0.1:8080/")]               // wrong port
    [InlineData("https://127.0.0.1:9931/")]              // wrong scheme
    [InlineData("http://sub.127.0.0.1.nip.io:9931/")]    // host prefix trick
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("about:blank")]
    public void ForeignOrigins_AreRefused(string uri)
    {
        Assert.False(WebOriginPolicy.IsServerOrigin(new Uri(uri), "127.0.0.1", 9931));
    }

    [Fact]
    public void RelativeAndNullUris_AreRefused()
    {
        Assert.False(WebOriginPolicy.IsServerOrigin(null, "127.0.0.1", 9931));
        Assert.False(WebOriginPolicy.IsServerOrigin(new Uri("/relative", UriKind.Relative), "127.0.0.1", 9931));
    }

    [Fact]
    public void CaseDifferences_InHostAndScheme_AreStillTheOrigin()
    {
        Assert.True(WebOriginPolicy.IsServerOrigin(new Uri("HTTP://LOCALHOST:9931/"), "localhost", 9931));
    }

    [Theory]
    [InlineData("http://example.com/page")]
    [InlineData("https://example.com/page")]
    [InlineData("HTTPS://example.com/page")]
    public void PlainHttpAndHttps_MayGoToTheBrowser(string uri)
    {
        Assert.True(WebOriginPolicy.IsOpenableExternal(new Uri(uri)));
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ms-appx:///Assets/logo.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("search:protocol-handler")]
    public void NonHttpSchemes_AreNotOpened(string uri)
    {
        Assert.False(WebOriginPolicy.IsOpenableExternal(new Uri(uri)));
    }

    [Fact]
    public void NullAndRelativeUri_AreNotOpened()
    {
        Assert.False(WebOriginPolicy.IsOpenableExternal(null));
        Assert.False(WebOriginPolicy.IsOpenableExternal(new Uri("relative/path", UriKind.Relative)));
    }
}
