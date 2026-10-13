using LlamaApp.Common;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the <c>llama://</c> deep-link parser. Deep-link URIs are
/// remote-controlled data — any app or website can ask Windows to activate
/// the app with one — so the parser must accept exactly the two documented
/// forms (chat, model), decode payloads safely, and reject everything else:
/// foreign schemes, unknown hosts, oversized payloads, unparsable URIs.
/// </summary>
public sealed class DeepLinkTests
{
    // ---- chat links ----

    [Theory]
    [InlineData("llama://chat", DeepLinkKind.Chat, null)]
    [InlineData("llama://chat?prompt=Hello%20world", DeepLinkKind.Chat, "Hello world")]
    [InlineData("llama://chat?prompt=", DeepLinkKind.Chat, null)]              // empty = just open the chat
    [InlineData("llama://chat?prompt", DeepLinkKind.Chat, null)]               // bare key, no '='
    [InlineData("llama://chat?other=1&prompt=second", DeepLinkKind.Chat, "second")]
    [InlineData("llama://CHAT?prompt=hi", DeepLinkKind.Chat, "hi")]             // host case-insensitive
    [InlineData("LLAMA://chat?prompt=hi", DeepLinkKind.Chat, "hi")]             // scheme case-insensitive
    public void ChatLinks_ProduceChatRequests(string uri, DeepLinkKind kind, string? prompt)
    {
        var link = DeepLink.Parse(uri);

        Assert.Equal(kind, link.Kind);
        Assert.Equal(prompt, link.Prompt);
        Assert.Null(link.ModelId);
    }

    [Fact]
    public void ChatPrompt_IsPercentDecoded_IncludingUrlSensitiveChars()
    {
        var link = DeepLink.Parse("llama://chat?prompt=a%26b%3Dc%2Bd%3Fe%3D%2520%22quoted%22");

        Assert.Equal("a&b=c+d?e=%20\"quoted\"", link.Prompt);
    }

    [Fact]
    public void ChatPrompt_DecodesUnicode()
    {
        var link = DeepLink.Parse("llama://chat?prompt=%F0%9F%A4%96%20hello");

        Assert.Equal("🤖 hello", link.Prompt);
    }

    [Fact]
    public void OversizedChatPrompt_IsDropped_ButTheChatStillOpens()
    {
        // A 64K+ prompt via URL is abuse — it is dropped (the link still opens
        // the chat, same as an empty prompt) rather than truncated into the
        // composer.
        var prompt = new string('a', DeepLink.MaxPromptChars + 1);
        var link = DeepLink.Parse($"llama://chat?prompt={prompt}");

        Assert.Equal(DeepLinkKind.Chat, link.Kind);
        Assert.Null(link.Prompt);
    }

    // ---- model links ----

    [Theory]
    [InlineData("llama://model/ggml-org%2Fgemma-4-E4B-it-GGUF%3AQ4_0",
                "ggml-org/gemma-4-E4B-it-GGUF:Q4_0")]          // percent-encoded slashes and colon
    [InlineData("llama://model/ggml-org/gemma-4-E4B-it-GGUF:Q4_0",
                "ggml-org/gemma-4-E4B-it-GGUF:Q4_0")]          // raw (unencoded) form also works
    [InlineData("llama://model/MiMo-V2.6-Distill-Qwen-9B-Q8_0.gguf",
                "MiMo-V2.6-Distill-Qwen-9B-Q8_0.gguf")]        // a bare file name
    [InlineData("llama://MODEL/ggml-org%2Frepo%3AQ4_0", "ggml-org/repo:Q4_0")]
    public void ModelLinks_ProduceLoadModelRequests(string uri, string expectedId)
    {
        var link = DeepLink.Parse(uri);

        Assert.Equal(DeepLinkKind.LoadModel, link.Kind);
        Assert.Equal(expectedId, link.ModelId);
        Assert.Null(link.Prompt);
    }

    [Fact]
    public void EmptyModelId_IsRejected()
    {
        Assert.Equal(DeepLinkRequest.None, DeepLink.Parse("llama://model/"));
        Assert.Equal(DeepLinkRequest.None, DeepLink.Parse("llama://model"));
    }

    [Fact]
    public void OversizedModelId_IsRejected()
    {
        var id = new string('a', DeepLink.MaxModelIdChars + 1);

        Assert.Equal(DeepLinkRequest.None, DeepLink.Parse($"llama://model/{id}"));
    }

    // ---- rejections ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a uri")]
    [InlineData("http://llama/chat")]                        // foreign scheme
    [InlineData("file:///C:/evil.gguf")]
    [InlineData("llama://download/evil")]                   // unknown host
    public void ForeignOrMalformedLinks_AreRejected(string? uri)
    {
        Assert.Equal(DeepLinkRequest.None, DeepLink.Parse(uri));
    }

    [Fact]
    public void ChatLink_IgnoresExcessPath_PromptStillHonored()
    {
        // The host decides the action; a path on the chat host is ignored.
        var link = DeepLink.Parse("llama://chat/extra?prompt=hi");

        Assert.Equal(DeepLinkKind.Chat, link.Kind);
        Assert.Equal("hi", link.Prompt);
    }

    // ---- handoff round-trip ----

    [Fact]
    public void ModelIdHandoff_RoundTripsThroughTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "llama-deeplink-" + Guid.NewGuid().ToString("N"));
        try
        {
            InstanceHandoff.WriteModelId("ggml-org/gemma-4-E4B-it-GGUF:Q4_0", path);

            var consumed = InstanceHandoff.TryConsume(path);

            Assert.Equal(HandoffKind.ModelId, consumed.Kind);
            Assert.Equal("ggml-org/gemma-4-E4B-it-GGUF:Q4_0", consumed.ModelId);
            Assert.Null(consumed.ModelPath);
            Assert.Null(consumed.Text);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ModelIdHandoff_ToleratesCRLF()
    {
        // A hand-edited or externally-written request may carry CR.
        var request = InstanceHandoff.Parse("modelid\r\nggml-org/repo:Q4_0\r\n");

        Assert.Equal(HandoffKind.ModelId, request.Kind);
        Assert.Equal("ggml-org/repo:Q4_0", request.ModelId);
    }
}
