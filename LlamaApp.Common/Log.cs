using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LlamaApp.Common;

/// <summary>Severity threshold; messages at or above <see cref="Log.Level"/> are emitted.</summary>
public enum LogLevel { Trace, Debug, Info, Warn, Error, None }

/// <summary>
/// Process-global, dependency-free logger. Writes each line to a daily rolling
/// file (<c>%LOCALAPPDATA%\LlamaApp\logs\LlamaApp-YYYYMMDD.log</c>) and, when the
/// app was launched from a terminal or an IDE that captures stdout (Rider, VS
/// Code, <c>dotnet run</c>), mirrors it to that stdout so logs stream live into
/// the shell / debug panel. The stdout mirror is a no-op when there's no parent
/// console or redirected handle (double-click launch), so the tray experience
/// stays silent. Thread-safe; never throws into the caller.
/// </summary>
public static class Log
{
    public static LogLevel Level { get; set; } = LogLevel.Info;
    private static int RetentionDays { get; set; } = 7;

    private static readonly object Gate = new();
    private static string? _logDir;
    private static string? _currentPath;
    private static string? _currentDate;
    private static StreamWriter? _writer;
    private static StreamWriter? _consoleWriter;
    private static bool _colorEnabled;
    private static bool _initialized;

    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr h, uint mode);

    /// <summary>
    /// Initializes the logger: pins <see cref="Level"/>, creates the log dir,
    /// sweeps old files, and opens the stdout mirror (when
    /// <paramref name="enableConsole"/> is true). Idempotent; also runs lazily
    /// on the first write so a forgotten <see cref="Initialize"/> never drops
    /// early logs.
    /// </summary>
    public static void Initialize(LogLevel? level = null, bool enableConsole = true)
    {
        lock (Gate)
        {
            if (level is { } l) Level = l;

            _logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LlamaApp", "logs");
            Try(() => Directory.CreateDirectory(_logDir));

            if (enableConsole && _consoleWriter is null)
                TryOpenStdout();

            SweepOldLogs();
            _initialized = true;
        }
    }

    // ---- Entry points (call-site context via [Caller*]) ----

    public static void Trace(string message, [CallerMemberName] string? m = null, [CallerFilePath] string? f = null, [CallerLineNumber] int l = 0)
        => Write(LogLevel.Trace, message, null, m, f, l);
    public static void Debug(string message, [CallerMemberName] string? m = null, [CallerFilePath] string? f = null, [CallerLineNumber] int l = 0)
        => Write(LogLevel.Debug, message, null, m, f, l);
    public static void Info(string message, [CallerMemberName] string? m = null, [CallerFilePath] string? f = null, [CallerLineNumber] int l = 0)
        => Write(LogLevel.Info, message, null, m, f, l);
    public static void Warn(string message, [CallerMemberName] string? m = null, [CallerFilePath] string? f = null, [CallerLineNumber] int l = 0)
        => Write(LogLevel.Warn, message, null, m, f, l);
    public static void Warn(Exception ex, string? message = null, [CallerMemberName] string? m = null, [CallerFilePath] string? f = null, [CallerLineNumber] int l = 0)
        => Write(LogLevel.Warn, message, ex, m, f, l);
    public static void Error(string message, [CallerMemberName] string? m = null, [CallerFilePath] string? f = null, [CallerLineNumber] int l = 0)
        => Write(LogLevel.Error, message, null, m, f, l);
    public static void Error(Exception ex, string? message = null, [CallerMemberName] string? m = null, [CallerFilePath] string? f = null, [CallerLineNumber] int l = 0)
        => Write(LogLevel.Error, message, ex, m, f, l);

    // ---- Core write path ----

    private static void Write(LogLevel level, string? message, Exception? exception, string? member, string? file, int line)
    {
        if (level < Level || Level == LogLevel.None) return;

        var text = FormatLine(level, message, exception, member, file, line);

        lock (Gate)
        {
            Try(() => { EnsureWriter(); _writer?.Write(text); _writer?.Flush(); });

            if (_consoleWriter is not null)
            {
                try { _consoleWriter.Write(_colorEnabled ? Colorize(level, text) : text); _consoleWriter.Flush(); }
                catch { _consoleWriter = null; } // stdout went away — stop retrying
            }

            if (Debugger.IsAttached)
                Debugger.Log(0, "LlamaApp", text);
        }
    }

    private static string FormatLine(LogLevel level, string? message, Exception? exception, string? member, string? file, int line)
    {
        var sb = new StringBuilder(256);
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
          .Append(" [").Append(LevelTag(level)).Append("] ")
          .Append('[').Append(Environment.CurrentManagedThreadId.ToString().PadLeft(2, ' ')).Append("] ");

        if (!string.IsNullOrEmpty(member))
        {
            sb.Append(member);
            if (file is not null)
                sb.Append(" (").Append(ShortFileName(file)).Append(':').Append(line).Append(')');
            sb.Append(' ');
        }

        if (!string.IsNullOrEmpty(message))
            sb.Append(message);

        if (exception is not null)
        {
            if (sb.Length > 0 && sb[^1] != '\n') sb.AppendLine();
            sb.Append("  Exception: ").Append(exception.GetType().FullName).Append(": ").AppendLine(exception.Message);
            for (var ex = exception.InnerException; ex is not null; ex = ex.InnerException)
                sb.Append("  --> ").Append(ex.GetType().Name).Append(": ").AppendLine(ex.Message);
            if (exception.StackTrace is { } trace)
                sb.Append("  StackTrace:\n").Append(trace);
        }

        if (sb.Length == 0 || sb[^1] != '\n') sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>Opens today's file, rotating at a calendar-day boundary. Caller holds <see cref="Gate"/>.</summary>
    private static void EnsureWriter()
    {
        if (!_initialized) Initialize();

        var today = DateTime.Now.ToString("yyyyMMdd");
        if (_writer is not null && _currentDate == today && _currentPath is not null)
            return;

        Try(() => _writer?.Dispose());
        _writer = null;
        _currentDate = today;

        if (string.IsNullOrEmpty(_logDir)) return;

        _currentPath = Path.Combine(_logDir, $"LlamaApp-{today}.log");
        try
        {
            // Append + FileShare.ReadWrite so the file can be opened in a text
            // editor / tail while the app keeps writing.
            var fs = new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096);
            _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false };
        }
        catch { _writer = null; }
    }

    /// <summary>Deletes daily log files older than <see cref="RetentionDays"/>.</summary>
    private static void SweepOldLogs()
    {
        if (string.IsNullOrEmpty(_logDir) || !Directory.Exists(_logDir)) return;
        var cutoff = DateTime.Now.AddDays(-RetentionDays);
        foreach (var f in Directory.EnumerateFiles(_logDir, "LlamaApp-*.log"))
            Try(() => { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); });
    }

    /// <summary>
    /// Opens a stdout mirror for the log. Two strategies, tried in order:
    /// <list type="number">
    /// <item><b>AttachConsole(ATTACH_PARENT_PROCESS)</b> — when launched from a
    /// terminal (cmd/PowerShell), attaches to the parent's console so we get a
    /// real console handle that supports ANSI VT color (<see cref="Colorize"/>).</item>
    /// <item><b>GetStdHandle directly</b> — when <c>AttachConsole</c> fails (no
    /// parent console), tries the process's inherited/redirected stdout handle.
    /// This is what Rider / VS Code / <c>dotnet run</c> provide: they redirect
    /// stdout to a pipe at process creation, so <c>GetStdHandle</c> returns a
    /// valid (pipe) handle without a console. No ANSI color (pipes don't support
    /// <c>GetConsoleMode</c>), but the text streams into the IDE's debug panel.</item>
    /// </list>
    /// No-op when neither path yields a valid handle (double-click / explorer).
    /// Caller holds <see cref="Gate"/>.
    /// </summary>
    private static void TryOpenStdout()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            // 1. Terminal launch: attach to the parent's console for a real
            //    (colorable) console handle.
            if (AttachConsole(ATTACH_PARENT_PROCESS))
            {
                OpenStdoutWriter(GetStdHandle(STD_OUTPUT_HANDLE), enableColor: true);
                return;
            }

            // 2. IDE / redirected-stdout launch (Rider, VS Code, dotnet run):
            //    AttachConsole failed (no parent console), but the process may
            //    still have a valid inherited/redirected stdout handle.
            OpenStdoutWriter(GetStdHandle(STD_OUTPUT_HANDLE), enableColor: false);
        }
        catch
        {
            _consoleWriter = null;
            _colorEnabled = false;
        }
    }

    /// <summary>
    /// Wraps <paramref name="handle"/> in a <see cref="StreamWriter"/> stored in
    /// <see cref="_consoleWriter"/>. When <paramref name="enableColor"/> is true
    /// (terminal console handle), enables ANSI VT processing for <see cref="Colorize"/>.
    /// A null/invalid handle is a no-op. Caller holds <see cref="Gate"/>.
    /// </summary>
    private static void OpenStdoutWriter(IntPtr handle, bool enableColor)
    {
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            if (enableColor) Try(() => FreeConsole()); // nothing to write to — release the attach
            return;
        }

        if (enableColor && GetConsoleMode(handle, out var mode))
            _colorEnabled = SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);

        // wrapsHandle=false: the console handle is owned/freed by our console
        // management (FreeConsole), not by the FileStream.
        _consoleWriter = new StreamWriter(
            new FileStream(new SafeFileHandle(handle, ownsHandle: false), FileAccess.Write),
            new UTF8Encoding(false)) { AutoFlush = false };
    }

    /// <summary>Wraps a line in ANSI color codes for the level (console only; file stays plain).</summary>
    private static string Colorize(LogLevel level, string text) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "\x1b[90m" + text + "\x1b[0m",   // gray
        LogLevel.Info  => "\x1b[37m" + text + "\x1b[0m",                    // white
        LogLevel.Warn  => "\x1b[33m" + text + "\x1b[0m",                    // yellow
        LogLevel.Error => "\x1b[31m" + text + "\x1b[0m",                    // red
        _ => text,
    };

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Info  => "INFO ",
        LogLevel.Warn  => "WARN ",
        LogLevel.Error => "ERROR",
        _              => "?????",
    };

    private static string ShortFileName(string path)
    {
        var idx = path.LastIndexOfAny(['/', '\\']);
        return idx >= 0 ? path[(idx + 1)..] : path;
    }

    /// <summary>Runs <paramref name="action"/> and swallows any exception (logging must never throw).</summary>
    private static void Try(Action action) { try { action(); } catch { /* best-effort */ } }
}