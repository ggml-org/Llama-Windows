using LlamaApp.Common;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the local-server access policy: when the llama server is bound
/// beyond loopback it needs an app-generated API key, otherwise it is an open
/// control API (model download/load/delete, chat) on the network.
/// </summary>
public sealed class ServerAuthTests
{
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.0.0.5", false)]   // any loopback address stays local
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not-an-ip", false)]
    [InlineData("::1", false)]          // IPv6 is not a valid bind here anyway
    [InlineData("0.0.0.0", true)]
    [InlineData("192.168.1.42", true)]
    [InlineData("10.0.0.5", true)]
    public void RequiresApiKey_MatchesWhetherBindExceedsThisMachine(string? address, bool expected)
    {
        Assert.Equal(expected, ServerAuth.RequiresApiKey(address));
    }

    [Fact]
    public void GenerateApiKey_Is64LowercaseHexChars()
    {
        var key = ServerAuth.GenerateApiKey();

        Assert.Equal(64, key.Length);
        Assert.All(key, c => Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'f', $"non-hex char: {c}"));
    }

    [Fact]
    public void GenerateApiKey_IsUnique()
    {
        Assert.NotEqual(ServerAuth.GenerateApiKey(), ServerAuth.GenerateApiKey());
    }
}
