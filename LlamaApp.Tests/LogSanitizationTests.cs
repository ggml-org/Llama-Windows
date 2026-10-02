using System.Text.RegularExpressions;
using LlamaApp.Common;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for the log line sanitizer in <see cref="Log"/>: CR/LF arriving
/// inside interpolated content (messages, exception texts — routinely
/// remote-controlled data: SSE error payloads, HTTP bodies) must not be able
/// to start a new log record (CWE-117 log injection). The contract: after the
/// first line of an entry, no line may begin with a fresh log-timestamp
/// prefix, while legitimate structure (the exception block's own line breaks
/// and stack traces) is preserved.
/// </summary>
public class LogSanitizationTests
{
    /// <summary>What a forged log record would look like to a log parser.</summary>
    private static readonly Regex ForgedRecordStart = new(
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[(TRACE|DEBUG|INFO|WARN|ERROR)\]",
        RegexOptions.Compiled);

    private static string Format(string? message, Exception? exception = null)
        => Log.FormatLine(LogLevel.Info, message, exception, "TestMethod", "file.cs", 42);

    [Theory]
    [InlineData("ok\r\n2026-01-01 00:00:00.000 [ERROR] forged entry")]
    [InlineData("ok\r2026-01-01 00:00:00.000 [ERROR] forged entry")]
    [InlineData("ok\n2026-01-01 00:00:00.000 [ERROR] forged entry")]
    [InlineData("ok\n\n\nanother")]
    public void LineBreaks_In_The_Message_Cannot_Start_A_Record(string hostileMessage)
    {
        var text = Format(hostileMessage);

        var lines = text.Replace("\r\n", "\n").Split('\n');
        // The first line is the app's own record; nothing after it may look
        // like one.
        Assert.All(lines.Skip(1), line => Assert.DoesNotMatch(ForgedRecordStart, line));
    }

    [Fact]
    public void LineBreaks_Become_Visible_Escapes_Keeping_The_Content_Readable()
    {
        var text = Format("a\r\nb\rc\nd");

        Assert.Contains("a\\r\\nb\\rc\\nd", text);
        // The literal escape sequences, not raw control characters.
        Assert.DoesNotContain("\r", text[..^Environment.NewLine.Length]);
        Assert.DoesNotContain("\n", text[..^Environment.NewLine.Length]);
    }

    [Fact]
    public void A_Message_Without_Line_Breaks_Passes_Through_Unchanged()
    {
        var text = Format("plain message with no special characters");

        Assert.Contains(" plain message with no special characters" + Environment.NewLine, text);
    }

    [Fact]
    public void Exception_Messages_Are_Sanitized_Too()
    {
        // Exception messages embed remote data just as often (an
        // HttpRequestException carries the server's response text).
        var ex = new Exception("outer\r\n2026-01-01 00:00:00.000 [ERROR] forged",
            new Exception("inner\n2026-01-01 00:00:00.000 [ERROR] forged too"));

        var text = Format("task failed", ex);

        var lines = text.Replace("\r\n", "\n").Split('\n');
        Assert.All(lines.Skip(1), line => Assert.DoesNotMatch(ForgedRecordStart, line));
        Assert.Contains("outer\\r\\n2026-01-01", text);
        Assert.Contains("inner\\n2026-01-01", text);
    }

    [Fact]
    public void StackTraces_Keep_Their_Structural_Newlines()
    {
        // A stack trace's newlines are the exception block's own structure —
        // they must survive so traces stay readable across lines.
        var ex = new SyntheticTraceException("boom");

        var text = Format("task failed", ex);

        Assert.Contains("  StackTrace:\n   at LlamaApp.Tests.Something.Do()", text);
        Assert.Contains(Environment.NewLine + "   at LlamaApp.Tests.SomethingElse()", text);
    }

    /// <summary>Exception with a deterministic multi-line stack trace.</summary>
    private sealed class SyntheticTraceException : Exception
    {
        public SyntheticTraceException(string message) : base(message) { }
        public override string? StackTrace =>
            "   at LlamaApp.Tests.Something.Do()\r\n   at LlamaApp.Tests.SomethingElse()";
    }

    [Fact]
    public void A_Clean_Failure_Report_Still_Fits_On_One_Line()
    {
        // The common case must remain the common shape: prefix + message +
        // exactly one trailing line terminator.
        var text = Format("llama.cpp runtime is up to date (installed b9553, latest b9553)");

        Assert.EndsWith(Environment.NewLine, text);
        Assert.Equal(1, text.Replace("\r\n", "\n").Split('\n').Length - 1);
    }
}
