using System.Text;

namespace LlamaApp.Llama;

/// <summary>
/// User-owned additions to the generated models-preset INI.
///
/// <para>The app regenerates <c>model-context-presets.ini</c> from its settings
/// and a scan of the model cache on every launch and preset reload, so anything
/// a user types into that file is lost on the next rewrite. This is the escape
/// hatch: a sibling file (<c>models.user.ini</c> under
/// <c>%USERPROFILE%\.llama</c>, next to the generated file the server reads)
/// the app only ever reads, merged into the generated content at write time.</para>
///
/// <para>Precedence is deliberately flat — a key present in the user file
/// always wins, including over values derived from memory profiling. A
/// conditional rule ("app wins on memory-safety keys") would make overrides
/// silently no-op, which is worse than letting someone pick a
/// <c>ctx-size</c> their machine can't hold: the failure is at least legible
/// and self-inflicted.</para>
///
/// <para>Thread-safe: the file is re-read on every preset write while UI
/// reads the cached override state on demand. Isolated tests construct their
/// own instance with a temp <see cref="FilePath"/>; the app uses
/// <see cref="Shared"/>.</para>
/// </summary>
public sealed class UserModelOverrides
{
    /// <summary>
    /// The app-wide instance, backed by <c>models.user.ini</c> in the shared
    /// per-user llama directory (see <see cref="FilePath"/>).
    /// </summary>
    public static UserModelOverrides Shared { get; } = new(DefaultFilePath());

    /// <summary>Filename of the user-owned file.</summary>
    public const string FileName = "models.user.ini";

    private static string DefaultFilePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".llama", FileName);

    /// <summary>
    /// Full path of the user-owned override file. Under
    /// <c>%USERPROFILE%\.llama</c> rather than the app-data directory: the
    /// server reads its preset file from outside the package identity, and
    /// this file's entire purpose is being hand-edited — it must be a plain
    /// location the user can find.
    /// </summary>
    public string FilePath { get; }

    /// <summary>Creates an instance over an explicit file path (isolated tests).</summary>
    internal UserModelOverrides(string filePath)
    {
        FilePath = filePath;
    }

    // ---- Suspended state ----

    /// <summary>
    /// The option the server refused, when overrides are currently suspended.
    ///
    /// <para><c>llama serve</c> treats an unrecognized option as fatal for the
    /// whole preset file, so a single typo takes every model down — and the
    /// app's own keys are always valid, meaning the culprit is necessarily a
    /// user key. When that happens the caller suspends overrides and retries,
    /// trading the user's config for an app that still runs. Cleared when the
    /// file changes (or is deleted), so fixing the typo is enough to get the
    /// overrides back.</para>
    ///
    /// <para>The name is retained rather than just a flag because the menu
    /// keeps showing it: the hint posted at the time is transient and easily
    /// missed, but the config stays ignored until the file is fixed.</para>
    /// </summary>
    public string? SuspendedOption { get; private set; }

    /// <summary>Whether overrides are currently suspended.</summary>
    public bool IsSuspended => SuspendedOption is not null;

    /// <summary>Whether the user file contributes anything right now.</summary>
    /// <remarks>
    /// Zero-pair sections never count: a scaffolded-but-unedited section
    /// merges as a no-op and must not look like an active override to the
    /// recovery path or the UI.
    /// </remarks>
    internal bool HasActiveOverrides
    {
        get
        {
            lock (_gate)
                return SuspendedOption is null && _sections.Any(s => s.Pairs.Count > 0);
        }
    }

    private readonly object _gate = new();

    /// <summary>
    /// Monotonic count of observed raw-content changes. Bumped only when a
    /// reload sees content different from the last read (including the first
    /// read and a deleted file), so it identifies the configuration a
    /// rejection was about: <see cref="Suspend(string, long)"/> conditions on
    /// it, and callers can read it around a write to know whether a later
    /// recovery is still about the configuration they rejected.
    /// </summary>
    internal long Generation { get { lock (_gate) return _generation; } }

    private long _generation;

    /// <summary>The most recently parsed override file, in file order.</summary>
    private List<Section> _sections = [];

    /// <summary>
    /// Raw text as of the last read — <see langword="null"/> for absent OR
    /// unreadable, since both end with empty sections — so an edit can be
    /// distinguished from a reread.
    /// </summary>
    private string? _rawText;

    /// <summary>
    /// Cached <c>ctx-size</c> lookup: model id → pinned value. Rebuilt only by
    /// <see cref="PublishOverrides"/> — from the configuration the server
    /// actually accepted — and cleared the moment overrides are suspended.
    /// That is what <see cref="OverriddenCtxSize"/> answers from, so it can
    /// never report a value the server refused.
    /// </summary>
    private Dictionary<string, string> _ctxSizes = new(StringComparer.Ordinal);

    /// <summary>
    /// The <see cref="Generation"/> <see cref="PublishOverrides"/> last
    /// materialized into <see cref="_ctxSizes"/>; negative while nothing is
    /// published (fresh instance, or after a suspension or a
    /// <see cref="InvalidatePublishedOverrides"/>).
    /// </summary>
    private long _publishedGeneration = -1;

    /// <summary>
    /// The <see cref="Generation"/> the current suspension is about. A later
    /// reload that bumps the generation past it clears the suspension: the
    /// refused content is gone, so the user's fix deserves a fresh attempt.
    /// </summary>
    private long _suspendedGeneration = -1;

    /// <summary>
    /// Writes a commented, inert template if the file isn't there yet.
    ///
    /// <para>Without this the feature is invisible: the file doesn't exist,
    /// nothing in the app mentions it, and users only learn about it from a
    /// GitHub thread. Seeding it means the answer to "can I set sampler
    /// params?" is a file already sitting in a directory people look in.</para>
    ///
    /// <para>Seeds whenever absent, with no "already seeded" flag — so
    /// deleting the file brings the template back on next launch. The content
    /// is entirely comments and has no effect on the server, so self-healing
    /// is a better trade than persisting state to honour a deletion. A file
    /// that appears between the check and the write is left alone
    /// (create-new), so racing seeds never clobber real content.</para>
    ///
    /// <para>Non-fatal on failure: overrides are opt-in, so a missing
    /// template costs discoverability, not function.</para>
    /// </summary>
    public void SeedTemplateIfNeeded()
    {
        try
        {
            if (File.Exists(FilePath)) return;
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // CreateNew: a file that appears between the check and the open
            // (a racing seed, or the user) wins untouched — seeds never
            // clobber real content.
            using var stream = new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(TemplateBytes);
        }
        catch (IOException) when (File.Exists(FilePath))
        {
            // CreateNew refused because the file now exists — see above.
        }
        catch (Exception ex)
        {
            Common.Log.Warn(ex, $"could not seed {FileName}");
        }
    }

    /// <summary>
    /// The seeded template, byte-exact (UTF-8 without BOM, LF line endings,
    /// ending with the last comment's newline and no trailing blank line —
    /// exactly what the macOS sibling app seeds): all comments, so parsing it
    /// yields no sections and the seeded file has no effect on the server.
    /// Frozen into <see cref="TemplateBytes"/> at init to remove any
    /// dependency on the compiler's string-literal newline policy.
    /// </summary>
    private const string Template = "; Custom settings for your models, applied on top of the ones the app picks.\n"
        + ";\n"
        + "; The app regenerates its own config from a scan of your model cache every\n"
        + "; launch. This file is yours -- the app only ever reads it, so your edits\n"
        + "; survive. Anything you set here wins, including context size.\n"
        + ";\n"
        + "; Use the same section headers the app uses (org/repo:QUANT). You only need\n"
        + "; the keys you want to change:\n"
        + ";\n"
        + ";   [ggml-org/gemma-4-E4B-it-GGUF:Q8_0]\n"
        + ";   temp = 0.7\n"
        + ";   top-p = 0.8\n"
        + ";   ctx-size = 32768\n"
        + ";\n"
        + "; A section for a model the app didn't find is passed through as-is, which\n"
        + "; is how you point at weights or a draft model from another repo:\n"
        + ";\n"
        + ";   [unsloth/DeepSeek-V4-Flash-0731-GGUF:UD-Q2_K_XL]\n"
        + ";   model = /path/to/DeepSeek-V4-Flash-UD-Q2_K_XL.gguf\n"
        + ";   spec-type = draft-dspark\n"
        + ";   spec-draft-model = /path/to/draft.gguf\n"
        + ";\n"
        + "; Keys are `llama serve` options without the leading dashes. Changes apply\n"
        + "; the next time the model list is refreshed -- restart the app if unsure.\n"
        + ";\n"
        + "; Setting ctx-size here takes over from the context picker in the app, which\n"
        + "; shows the pinned value instead. Remove the key to get the picker back.\n";

    /// <summary><see cref="Template"/> as exact file bytes.</summary>
    internal static readonly byte[] TemplateBytes = Encoding.UTF8.GetBytes(Template);

    /// <summary>
    /// The <c>ctx-size</c> the user pinned for <paramref name="modelId"/>,
    /// or <see langword="null"/> when there is none — or when nothing has been
    /// published yet. While suspended this is always <see langword="null"/>,
    /// since the overrides aren't applied then. Answered from a cache that
    /// <see cref="PublishOverrides"/> rebuilds from the last ACCEPTED
    /// configuration, so it is effective across repeated sections for the same
    /// id (the value is whatever the merged file uses) and never reports a
    /// value from content the server has not accepted.
    /// </summary>
    public string? OverriddenCtxSize(string modelId)
    {
        lock (_gate)
        {
            return SuspendedOption is null && _ctxSizes.TryGetValue(modelId, out var value)
                ? value
                : null;
        }
    }

    /// <summary>
    /// Appends an empty <c>[<paramref name="modelId"/>]</c> section to the
    /// user file, creating the file from the template first when it is
    /// missing. Idempotent: a file that already contains the section is left
    /// untouched. Append-only — existing bytes and section order are
    /// preserved exactly — so a user's edits and comments are never rewritten.
    /// Returns the <see cref="FilePath"/>.
    ///
    /// <para><paramref name="modelId"/> must be safe to head a section (no
    /// brackets, no control characters); an unsafe id is rejected before any
    /// I/O happens. Opening the file is the UI's job.</para>
    ///
    /// <para>A file whose BOM says it is not UTF-8 (UTF-16/UTF-32, in either
    /// byte order) is rejected with <see cref="InvalidDataException"/> before
    /// anything is written: the server reads this file as UTF-8, so such a
    /// file is already broken, and appending UTF-8 bytes would only mix the
    /// encodings and hide the real problem. A UTF-8 BOM is fine.</para>
    /// </summary>
    public string EnsureSection(string modelId)
    {
        if (!LlamaManager.IsSafeSectionId(modelId))
            throw new ArgumentException($"model id cannot head an INI section safely: {modelId}", nameof(modelId));

        lock (_gate)
        {
            if (File.Exists(FilePath))
            {
                // Read the real bytes once: the BOM check needs them (a
                // decoded string hides which encoding the file declared), and
                // idempotence is checked from the real content so a hand-added
                // header counts too. Parse keeps the FIRST of repeated section
                // names, so this also finds a coalesced id.
                var bytes = ReadAllBytesOrEmpty();
                RejectNonUtf8(bytes);
                if (Parse(DecodeUtf8(bytes)).Any(s => s.Name == modelId)) return FilePath;
                AppendNewSection(modelId);
            }
            else
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                // CreateNew keeps a racing writer's file from being clobbered;
                // the template prefix goes in before the section.
                using var stream = new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                stream.Write(TemplateBytes);
                AppendNewSection(modelId, stream);
            }
            return FilePath;
        }
    }

    /// <summary>
    /// Throws <see cref="InvalidDataException"/> when <paramref name="bytes"/>
    /// begin with a BOM that declares a non-UTF-8 encoding (UTF-16 LE/BE,
    /// UTF-32 LE/BE, UTF-7). A UTF-8 BOM — or no BOM — passes. Used before
    /// writing, so a file in the wrong encoding never gets UTF-8 bytes
    /// appended to it (the server reads this file as UTF-8, so mixing
    /// encodings only buries the real problem).
    /// </summary>
    private static void RejectNonUtf8(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return; // UTF-8 BOM

        // UTF-16 LE (FF FE) — also the head of UTF-32 LE (FF FE 00 00).
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            throw new InvalidDataException($"{FileName} is UTF-16/UTF-32; save it as UTF-8");
        // UTF-16 BE (FE FF).
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            throw new InvalidDataException($"{FileName} is UTF-16/UTF-32; save it as UTF-8");
        // UTF-32 BE (00 00 FE FF).
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            throw new InvalidDataException($"{FileName} is UTF-16/UTF-32; save it as UTF-8");
        // UTF-7 (2B 2F 76, then 38 / 39 / 2B / 2F).
        if (bytes.Length >= 4 && bytes[0] == 0x2B && bytes[1] == 0x2F && bytes[2] == 0x76)
            throw new InvalidDataException($"{FileName} is not UTF-8; save it as UTF-8");

        // No BOM: UTF-16/32 without one still can't be UTF-8 text — NUL bytes
        // pad every ASCII character. Valid UTF-8 INI content never contains
        // one, so a NUL up front means the file is in a wide encoding.
        var scan = Math.Min(bytes.Length, 512);
        for (var i = 0; i < scan; i++)
            if (bytes[i] == 0x00)
                throw new InvalidDataException($"{FileName} is not UTF-8; save it as UTF-8");
    }

    /// <summary>
    /// Decodes <paramref name="bytes"/> as UTF-8, dropping a leading UTF-8
    /// BOM the way <see cref="File.ReadAllText(string)"/> would — the BOM is
    /// encoding metadata, not part of the first section name.
    /// </summary>
    private static string DecodeUtf8(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    /// <summary>
    /// Reloads the user file, then returns <paramref name="generatedIni"/>
    /// with the overrides merged in — the bytes the server should be handed.
    ///
    /// <para>With no active overrides (no file, unreadable file, empty
    /// file, zero-pair sections only, or suspended) the generated bytes come
    /// back untouched — the very same string, not a re-serialization — so the
    /// server's view is unchanged for users who never touch the feature.</para>
    ///
    /// <para>This only STAGES the current file content (and its generation)
    /// for merge and recovery — it never publishes the <c>ctx-size</c> cache.
    /// The server has not accepted this configuration yet; callers publish
    /// via <see cref="PublishOverrides"/> only once it has (an app-managed
    /// startup, or an accepted preset reload).</para>
    /// </summary>
    internal string ReloadAndApply(string generatedIni)
    {
        lock (_gate)
        {
            ReloadCore();
            if (SuspendedOption is not null || !_sections.Any(s => s.Pairs.Count > 0))
                return generatedIni;
            return Serialize(Apply(Parse(generatedIni), _sections));
        }
    }

    /// <summary>
    /// Publishes the staged override state as accepted by the server: the
    /// <see cref="OverriddenCtxSize"/> cache is rebuilt from the current
    /// sections, reflecting the configuration that is actually running. A
    /// no-op while suspended (the suspension already cleared the cache) and
    /// when the staged generation was already published, so repeated
    /// successful writes cannot resurrect values from an older configuration.
    /// Unconditional: prefer <see cref="PublishOverrides(long)"/>, which
    /// refuses to publish content the server never saw.
    /// </summary>
    internal void PublishOverrides()
    {
        lock (_gate)
            PublishCore();
    }

    /// <summary>
    /// Generation-guarded publication: takes effect only when the staged
    /// configuration is still the one at <paramref name="expectedGeneration"/>
    /// — the generation captured around the write that produced the
    /// acceptance. If the file changed since (a reload bumped the
    /// generation), the acceptance belongs to content that is already gone,
    /// and publishing would pin values from a configuration the server never
    /// accepted: this is a no-op. Any value that differs from the current
    /// <see cref="Generation"/> is a mismatch; use the parameterless overload
    /// to publish unconditionally.
    /// </summary>
    internal void PublishOverrides(long expectedGeneration)
    {
        lock (_gate)
        {
            if (_generation != expectedGeneration) return;
            PublishCore();
        }
    }

    /// <summary>
    /// Clears the published state — the <see cref="OverriddenCtxSize"/> cache
    /// and the published-generation marker — while leaving the staged raw
    /// sections, <see cref="Generation"/>, and any suspension untouched. The
    /// values of the last accepted configuration say nothing about a server
    /// that is not the one that accepted them, so on a server identity change
    /// they must stop being reported: the next
    /// <see cref="PublishOverrides(long)"/> on an accepted configuration
    /// stages the current sections again.
    /// </summary>
    internal void InvalidatePublishedOverrides()
    {
        lock (_gate)
        {
            _ctxSizes = new Dictionary<string, string>(StringComparer.Ordinal);
            _publishedGeneration = -1;
        }
    }

    /// <summary>
    /// Publication itself. Caller holds <see cref="_gate"/>.
    /// </summary>
    private void PublishCore()
    {
        if (SuspendedOption is not null || _publishedGeneration == _generation) return;
        RebuildCtxCache();
        _publishedGeneration = _generation;
    }

    /// <summary>
    /// Suspends overrides after the server refused <paramref name="option"/>
    /// (an unknown option is fatal to the whole preset file, and the app's own
    /// keys are always valid, so the culprit is a user key). The next reload
    /// that sees different file content — or a deleted file — re-enables the
    /// overrides.
    /// </summary>
    internal void Suspend(string option) => Suspend(option, expectedGeneration: -1);

    /// <summary>
    /// Conditional suspension: takes effect only when the staged configuration
    /// is still the one at <paramref name="expectedGeneration"/> — the
    /// generation captured around the write that produced the rejection. If
    /// the file changed since (a reload bumped the generation), the rejection
    /// belongs to configuration already gone, and suspending would freeze a
    /// newer, possibly valid file: this is a no-op. Pass a negative generation
    /// to suspend unconditionally (the stateless overload). Either way the
    /// <c>ctx-size</c> cache is cleared immediately on suspension: until the
    /// next accepted startup, no pinned value is known to be in effect.
    /// </summary>
    /// <returns><see langword="true"/> when the suspension took effect.</returns>
    internal bool Suspend(string option, long expectedGeneration)
    {
        lock (_gate)
        {
            if (expectedGeneration >= 0 && _generation != expectedGeneration) return false;
            SuspendedOption = option;
            _suspendedGeneration = _generation;
            _ctxSizes = new Dictionary<string, string>(StringComparer.Ordinal);
            _publishedGeneration = -1;
            return true;
        }
    }

    /// <summary>
    /// Re-reads the file. Caller holds <see cref="_gate"/>.
    ///
    /// <para>Content changes advance <see cref="Generation"/> and drop a
    /// suspension when it is about older content (fixing the file is enough
    /// to get the overrides back). The <c>ctx-size</c> cache is deliberately
    /// NOT rebuilt here — publication is <see cref="PublishOverrides"/>'s
    /// job.</para>
    /// </summary>
    private void ReloadCore()
    {
        var raw = ReadRawTextOrNull();
        if (raw == _rawText) return; // unchanged (including absent→absent): keep state
        _rawText = raw;
        _sections = raw is null ? [] : Parse(raw);
        _generation++;
        if (SuspendedOption is not null && _suspendedGeneration != _generation)
            SuspendedOption = null; // any edit deserves a fresh attempt
    }

    private string? ReadRawTextOrNull()
    {
        try
        {
            return File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
        }
        catch (Exception ex)
        {
            // A file that exists but can't be read is a user who thinks they
            // configured something and hasn't — worth logging. Absent is the
            // overwhelmingly common case and is not an error.
            Common.Log.Warn(ex, $"could not read {FileName} at {FilePath}");
            return null;
        }
    }

    private byte[] ReadAllBytesOrEmpty()
    {
        try
        {
            return File.ReadAllBytes(FilePath);
        }
        catch (Exception ex)
        {
            Common.Log.Warn(ex, $"could not read {FileName} at {FilePath}");
            return [];
        }
    }

    private void RebuildCtxCache()
    {
        // Later sections override earlier ones; within a section `set`
        // already reduced repeated keys to their last occurrence — the same
        // effective value the merged file gets. Keys are already canonical
        // (see <see cref="CanonicalKey"/>), so an alias pins the cache too.
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var section in _sections)
            foreach (var (key, value) in section.Pairs)
                if (string.Equals(key, "ctx-size", StringComparison.Ordinal))
                    map[section.Name] = value;
        _ctxSizes = map;
    }

    /// <summary>
    /// Appends <c>[<paramref name="modelId"/>]</c> plus its trailing blank
    /// line to the user file, terminating the last line first when it is
    /// unterminated (a trailing comment included) so the header can never
    /// join it. Appending leaves every existing byte and section untouched.
    /// Caller holds <see cref="_gate"/>; <paramref name="stream"/> is the
    /// just-created file, already holding the template, when the file was
    /// missing.
    /// </summary>
    private void AppendNewSection(string modelId, FileStream? stream = null)
    {
        var block = Encoding.UTF8.GetBytes($"[{modelId}]\n\n");
        if (stream is not null)
        {
            stream.Write(block);
            return;
        }

        using var file = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var needsNewline = false;
        if (file.Length > 0)
        {
            file.Seek(-1, SeekOrigin.End);
            needsNewline = file.ReadByte() != '\n';
        }
        // Seek between the read and the writes (mixed-direction FileStream
        // requirement), then append: one byte of separator at most.
        file.Seek(0, SeekOrigin.End);
        if (needsNewline) file.WriteByte((byte)'\n');
        file.Write(block);
    }

    // ---- Test hooks (pure, instance state never touched) ----

    /// <summary>
    /// The canonical names of generated options and the user aliases
    /// <c>llama serve</c> accepts for them — short flags, long names, and
    /// <c>LLAMA_ARG_*</c> environment spellings — keyed by each alias's own
    /// spelling. Aliases are canonicalized at parse time — in both
    /// directions, so a user
    /// writing the short <c>-c</c> spelling and one writing the long
    /// <c>--ctx-size</c> spelling merge as the SAME key — otherwise an alias
    /// would not replace the generated canonical key and the merged file
    /// would end up passing both to the server, which of the two wins is
    /// whatever <c>std::map</c> ordering decided.
    /// </summary>
    private static readonly Dictionary<string, string> OptionAliases =
        new(StringComparer.Ordinal)
        {
            // {"-c", "--ctx-size"}
            ["c"] = "ctx-size",
            ["ctx-size"] = "ctx-size",
            // {"-m", "--model"}
            ["m"] = "model",
            ["model"] = "model",
            // {"--spec-draft-model", "-md", "--model-draft"}
            ["md"] = "spec-draft-model",
            ["model-draft"] = "spec-draft-model",
            ["spec-draft-model"] = "spec-draft-model",
            // {"-mm", "--mmproj"}
            ["mm"] = "mmproj",
            ["mmproj"] = "mmproj",
            // Environment-variable spellings of the same options (`llama
            // serve` reads LLAMA_ARG_CTX_SIZE for --ctx-size and friends):
            // one more spelling of the same key that must merge, not double.
            ["LLAMA_ARG_CTX_SIZE"] = "ctx-size",
            ["LLAMA_ARG_MODEL"] = "model",
            ["LLAMA_ARG_MMPROJ"] = "mmproj",
            ["LLAMA_ARG_SPEC_TYPE"] = "spec-type",
            ["LLAMA_ARG_SPEC_DRAFT_MODEL"] = "spec-draft-model",
            ["spec-type"] = "spec-type",
        };

    /// <summary>
    /// The canonical spelling of an option key: known aliases of generated
    /// options — short flags, long names, and <c>LLAMA_ARG_*</c> environment
    /// spellings — map to the name the app generates, anything else passes through
    /// unchanged (unknown keys are the user's own and reach the server as
    /// typed — a typo there is diagnosed by the server, not silently renamed).
    /// </summary>
    internal static string CanonicalKey(string key)
        => OptionAliases.TryGetValue(key, out var canonical) ? canonical : key;

    /// <summary>
    /// Parses <paramref name="text"/> into ordered sections. Minimal INI
    /// grammar, matched to what <c>llama serve</c> accepts in a preset file:
    /// <c>[section]</c> headers and <c>key = value</c> lines. A header line may
    /// carry a trailing <c>;</c>/<c>#</c> comment after the closing bracket
    /// (<c>[org/a:Q4_0] ; hi</c>) — that must parse as a header, not a comment,
    /// or every following key lands on the previous model and
    /// <see cref="EnsureSection"/> cannot find the section. Key lines drop an
    /// inline <c>;</c>/<c>#</c> comment and any trailing whitespace from the
    /// value. Standalone comments and blank lines are dropped — the output is
    /// a generated file users aren't meant to edit, so carrying comments into
    /// it would invite editing the wrong file. Keys before any section header
    /// are ignored: the preset file has no global section, so they'd have
    /// nowhere to go. Repeated sections are coalesced into the first
    /// occurrence; repeated keys keep their last value. Values may contain
    /// <c>=</c> (e.g. JSON) — only the first separator splits. Keys that alias
    /// generated options are canonicalized (see <see cref="CanonicalKey"/>).
    /// </summary>
    internal static List<Section> Parse(string text)
    {
        var sections = new List<Section>();
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var current = -1; // parse cursor: section new keys land in

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');

            // Header first — a header may carry a trailing comment, so only
            // once it's ruled out does a `;`/`#` line start a comment.
            var header = ParseHeaderLine(line);
            if (header is not null)
            {
                if (header.Length == 0) { current = -1; continue; }
                if (indexByName.TryGetValue(header, out var existing))
                    current = existing; // repeated section: keys merge into the first
                else
                {
                    current = sections.Count;
                    indexByName[header] = current;
                    sections.Add(new Section(header));
                }
                continue;
            }

            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;

            // `value` may itself contain `=` (e.g. `chat-template-kwargs`
            // JSON), so split on the first separator only.
            var separator = line.IndexOf('=');
            if (separator < 0 || current < 0) continue;
            var key = line[..separator].Trim();
            if (key.Length == 0) continue;

            sections[current].Set(CanonicalKey(key), TrimInlineComment(line[(separator + 1)..]));
        }

        return sections;
    }

    /// <summary>
    /// Returns the section name of a header line — allowing trailing
    /// whitespace and a <c>;</c>/<c>#</c> comment after the closing bracket —
    /// or <see langword="null"/> when the line is not a header. The name is
    /// <see cref="string.Empty"/> for a header with no name (<c>[]</c>), which
    /// callers treat as ending the current section rather than joining it.
    /// </summary>
    private static string? ParseHeaderLine(string line)
    {
        if (!line.StartsWith('[')) return null;
        var close = line.IndexOf(']');
        if (close < 0) return null;
        var rest = line[(close + 1)..].Trim();
        if (rest.Length > 0 && rest[0] is not (';' or '#')) return null;
        return line[1..close].Trim();
    }

    /// <summary>
    /// Strips an inline <c>;</c>/<c>#</c> comment from a raw value and trims
    /// what remains — the exact place the server stops reading a value. JSON
    /// values (the ones carrying <c>=</c>) are passed through untouched: no
    /// generated value contains <c>;</c>/<c>#</c>, and mangling a structured
    /// value would be worse than leaving a user's quoted comment in it.
    /// </summary>
    private static string TrimInlineComment(string rawValue)
    {
        var trimmedStart = rawValue.TrimStart();
        if (trimmedStart.StartsWith('{') || trimmedStart.StartsWith('['))
            return rawValue.Trim();
        var comment = trimmedStart.IndexOfAny([';', '#']);
        return (comment < 0 ? trimmedStart : trimmedStart[..comment]).TrimEnd();
    }

    /// <summary>
    /// Applies user overrides to app-generated sections. Matching sections
    /// are merged per-key — user wins, replacing in place so app-generated
    /// keys keep their original position — so overriding <c>temp</c> doesn't
    /// require restating the <c>model</c>/<c>mmproj</c>/<c>spec-draft-model</c>
    /// paths. Sections that match nothing the app generated are appended
    /// verbatim and in file order — that's what lets someone define a model
    /// the scan can't produce, such as a draft model from another repo. Empty
    /// user sections (an <see cref="EnsureSection"/> scaffold the user hasn't
    /// filled in) are skipped entirely: they stay on disk for editing but
    /// contribute nothing — never a merge, never a ghost appended section.
    /// With no active overrides the input comes back unchanged.
    /// </summary>
    internal static List<Section> Apply(List<Section> generated, List<Section> overrides)
    {
        var merged = new List<Section>(generated.Count + overrides.Count);
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var section in generated)
        {
            indexByName[section.Name] = merged.Count;
            merged.Add(section.Clone());
        }

        foreach (var over in overrides)
        {
            if (over.Pairs.Count == 0) continue; // edit scaffold: nothing to say yet
            if (indexByName.TryGetValue(over.Name, out var index))
            {
                foreach (var (key, value) in over.Pairs)
                    merged[index].Set(key, value);
            }
            else
            {
                indexByName[over.Name] = merged.Count;
                merged.Add(over.Clone());
            }
        }

        return merged;
    }

    /// <summary>
    /// Serializes merged sections back to INI text: <c>[name]</c>, then
    /// <c>key = value</c> per pair, then a blank line per section — the exact
    /// shape <c>RenderModelsPresetIni</c> produces, so applying no overrides
    /// is byte-identical to the legacy output.
    /// </summary>
    internal static string Serialize(List<Section> sections)
    {
        var sb = new StringBuilder();
        foreach (var section in sections)
        {
            sb.Append('[').Append(section.Name).Append(']').Append('\n');
            foreach (var (key, value) in section.Pairs)
                sb.Append(key).Append(" = ").Append(value).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // ---- Section model ----

    /// <summary>
    /// One <c>[section]</c> and its keys, in the order they were written.
    /// Order is preserved rather than using a dictionary so that a section the
    /// app doesn't generate can be passed through looking like what the user
    /// typed — these files get hand-edited and diffed.
    /// </summary>
    internal sealed class Section
    {
        public Section(string name) => Name = name;

        public string Name { get; }

        /// <summary>Key/value pairs in order; repeated keys are coalesced.</summary>
        public List<KeyValuePair<string, string>> Pairs { get; } = [];

        /// <summary>
        /// Sets <paramref name="key"/>, replacing an existing entry in place
        /// or appending a new one. In-place replacement keeps generated keys
        /// in their original position when a user overrides one.
        /// </summary>
        public void Set(string key, string value)
        {
            for (var i = 0; i < Pairs.Count; i++)
            {
                if (Pairs[i].Key != key) continue;
                Pairs[i] = new KeyValuePair<string, string>(key, value);
                return;
            }
            Pairs.Add(new KeyValuePair<string, string>(key, value));
        }

        public Section Clone()
        {
            var copy = new Section(Name);
            copy.Pairs.AddRange(Pairs);
            return copy;
        }
    }
}
