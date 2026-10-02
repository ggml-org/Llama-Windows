using System.Text.Json;
using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Unit tests for <see cref="LlamaManager.ExtractFailureDetail"/>: the
/// <c>download_failed</c> SSE payload is inspected for a human-readable error
/// string and everything else — including llama.cpp master's current bare
/// <c>data:{}</c> and an absent <c>data</c> field — yields <c>null</c> rather
/// than throwing or fabricating text.
/// </summary>
public class SseFailureExtractionTests
{
    private static string? Extract(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return LlamaManager.ExtractFailureDetail(doc.RootElement);
    }

    [Fact]
    public void Absent_Data_Yields_Null()
    {
        // ParseSseStreamAsync hands a default (Undefined) JsonElement when the
        // event has no "data" field — TryGetProperty would throw on it.
        Assert.Null(LlamaManager.ExtractFailureDetail(default));
    }

    [Fact]
    public void Empty_Data_Object_Yields_Null()
        // llama.cpp master sends download_failed with data:{} — no error text.
        => Assert.Null(Extract("{}"));

    [Fact]
    public void String_Error_Field_Is_Extracted()
        => Assert.Equal("boom", Extract("""{"error":"boom"}"""));

    [Fact]
    public void Nested_Error_Message_Is_Extracted()
        => Assert.Equal("nested boom", Extract("""{"error":{"message":"nested boom"}}"""));

    [Fact]
    public void Top_Level_Message_Field_Is_Extracted()
        => Assert.Equal("top boom", Extract("""{"message":"top boom"}"""));

    [Fact]
    public void String_Error_Wins_Over_Message()
        => Assert.Equal("err", Extract("""{"error":"err","message":"msg"}"""));

    [Theory]
    [InlineData("""{"error":123}""")]
    [InlineData("""{"error":["a","b"]}""")]
    [InlineData("""{"error":{"message":42}}""")]
    [InlineData("""{"error":{"code":42}}""")]
    [InlineData("""{"message":{"a":1}}""")]
    [InlineData("""{"message":true}""")]
    [InlineData("""{"error":""}""")]
    [InlineData("""{"error":"   "}""")]
    [InlineData("""{"message":"\n\t"}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""42""")]
    public void Non_String_Blank_Or_Non_Object_Payloads_Yield_Null(string json)
        => Assert.Null(Extract(json));

    [Fact]
    public void Malformed_Shapes_Never_Throw()
    {
        // A nested error object whose message is itself an object (not a
        // string) must fall through to null, not throw.
        Assert.Null(Extract("""{"error":{"message":{"deep":true}},"message":null}"""));
    }
}
