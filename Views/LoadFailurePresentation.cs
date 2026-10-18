namespace LlamaApp.Views;

/// <summary>
/// Wording for failed model loads — pure so the mapping stays unit-testable.
/// The server reports a failed load as a dead child process (an exit code,
/// no message): turning that into something a user can act on is the whole
/// job here. The exit codes are NTSTATUS values (the child is a Windows
/// process); the known ones map to plain language, everything else reports
/// the raw code so bug reports carry it.
/// </summary>
public static class LoadFailurePresentation
{
    /// <summary>0xC0000409 — the process aborted via __fastfail. On model
    /// loads this is most often the CUDA backend's hard abort on a GPU
    /// out-of-memory (verified against a live repro).</summary>
    private const long FailFastAbort = -1073740791; // 0xC0000409

    /// <summary>0xC0000005 — access violation.</summary>
    private const long AccessViolation = -1073741819; // 0xC0000005

    /// <summary>0xC000013A — killed from the console (not a crash).</summary>
    private const long CtrlCExit = -1073741510; // 0xC000013A

    /// <summary>
    /// The tooltip for a failed load: the plain-language cause when the exit
    /// code is one we know, the raw code (signed and hex) otherwise — always
    /// ending with the hint to retry smaller (a smaller quant or a smaller
    /// context), which is the actionable path for the common case.
    /// </summary>
    public static string Describe(long? exitCode)
    {
        var cause = exitCode switch
        {
            FailFastAbort => "the model process stopped unexpectedly — most often the model does not fit in GPU memory",
            AccessViolation => "the model process crashed (access violation) — the model file may be corrupt or the runtime misbehaves on this model",
            CtrlCExit => "the load was interrupted",
            null => "the server never confirmed the load",
            _ => $"the model process stopped with exit code {exitCode} (0x{unchecked((uint)exitCode.Value):X8})",
        };
        return $"Couldn't load the model: {cause}. Try again — with a smaller quant or context if it ran out of memory.";
    }
}
