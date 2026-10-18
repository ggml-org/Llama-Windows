using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="LoadFailurePresentation.Describe"/> — turning the
/// server's bare exit code into something a user can act on. The codes are
/// NTSTATUS values from the dead model process; known ones get plain
/// language, unknown ones keep the raw code (bug reports need it).
/// </summary>
public class LoadFailurePresentationTests
{
    private const long FailFastAbort = -1073740791;      // 0xC0000409
    private const long AccessViolation = -1073741819;    // 0xC0000005
    private const long CtrlCExit = -1073741510;          // 0xC000013A

    [Fact]
    public void The_Gpu_Oom_Abort_Reads_As_Memory_Trouble()
    {
        var text = LoadFailurePresentation.Describe(FailFastAbort);

        Assert.Contains("GPU memory", text);
        Assert.Contains("smaller quant", text);
    }

    [Fact]
    public void An_Access_Violation_Reads_As_A_Crash()
        => Assert.Contains("crashed", LoadFailurePresentation.Describe(AccessViolation));

    [Fact]
    public void A_Ctrl_C_Exit_Reads_As_Interrupted()
        => Assert.Contains("interrupted", LoadFailurePresentation.Describe(CtrlCExit));

    [Fact]
    public void Unknown_Codes_Keep_The_Raw_Value()
    {
        var text = LoadFailurePresentation.Describe(-123456789);

        Assert.Contains("-123456789", text);
        Assert.Contains("0xF8A432EB", text); // two's complement hex
    }

    [Fact]
    public void A_Missing_Code_Reads_As_An_Unconfirmed_Load()
        => Assert.Contains("never confirmed", LoadFailurePresentation.Describe(null));

    [Fact]
    public void Every_Message_Keeps_The_Actionable_Hint()
    {
        foreach (long? code in new long?[] { FailFastAbort, AccessViolation, CtrlCExit, -1, null })
            Assert.Contains("Try again", LoadFailurePresentation.Describe(code));
    }
}
