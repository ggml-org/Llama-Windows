using LlamaApp.Common;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for the free-form "custom serve arguments" tokenizer: the text a
/// user types (or pastes from the llama.cpp docs) must become the exact argv
/// tokens the server receives — whitespace-separated, with quoting for values
/// that contain spaces, and a hard error for an open quote.
/// </summary>
public sealed class ArgumentTokenizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Tokenize_BlankInput_YieldsNoTokens(string? text)
    {
        Assert.Empty(ArgumentTokenizer.Tokenize(text));
    }

    [Fact]
    public void Tokenize_SplitsOnWhitespace()
    {
        Assert.Equal(
            new[] { "--threads", "4", "--flash-attn" },
            ArgumentTokenizer.Tokenize("--threads 4 --flash-attn"));
    }

    [Fact]
    public void Tokenize_NewlinesAreJustWhitespace()
    {
        // The settings box is multi-line; each flag on its own line is the
        // most readable way to keep a long custom list organized.
        Assert.Equal(
            new[] { "--threads", "4", "--flash-attn" },
            ArgumentTokenizer.Tokenize("--threads 4\r\n--flash-attn\n"));
    }

    [Fact]
    public void Tokenize_KeepsQuotedValueAsOneToken()
    {
        Assert.Equal(
            new[] { "--alias", "My Fine Model" },
            ArgumentTokenizer.Tokenize("--alias \"My Fine Model\""));
    }

    [Fact]
    public void Tokenize_SingleQuotesAreLiteral()
    {
        Assert.Equal(
            new[] { "--alias", "C:\\path with spaces" },
            ArgumentTokenizer.Tokenize("--alias 'C:\\path with spaces'"));
    }

    [Fact]
    public void Tokenize_EscapedQuoteInsideDoubleQuotes()
    {
        Assert.Equal(
            new[] { "--alias", "say \"hi\"" },
            ArgumentTokenizer.Tokenize("--alias \"say \\\"hi\\\"\""));
    }

    [Fact]
    public void Tokenize_BackslashBeforeOtherChars_IsLiteral()
    {
        // Windows paths: a backslash that isn't escaping a quote or another
        // backslash must survive verbatim.
        Assert.Equal(
            new[] { "--model", @"C:\models\file.gguf" },
            ArgumentTokenizer.Tokenize("--model \"C:\\models\\file.gguf\""));
    }

    [Fact]
    public void Tokenize_EqualsFormStaysOneToken()
    {
        Assert.Equal(
            new[] { "--foo=bar" },
            ArgumentTokenizer.Tokenize("--foo=bar"));
    }

    [Fact]
    public void Tokenize_OpenDoubleQuote_Throws()
    {
        Assert.Throws<FormatException>(
            () => ArgumentTokenizer.Tokenize("--alias \"My Model"));
    }

    [Fact]
    public void Tokenize_OpenSingleQuote_Throws()
    {
        Assert.Throws<FormatException>(
            () => ArgumentTokenizer.Tokenize("--alias 'My Model"));
    }
}
