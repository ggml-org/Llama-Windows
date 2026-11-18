using System.Text;
using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Tests for <see cref="UserModelOverrides"/> — the shared core of the
/// per-model user override file (<c>models.user.ini</c>): exact template
/// seeding, the minimal INI parse, the user-wins merge into the generated
/// models-preset INI, the raw-content-based suspension lifecycle, the cached
/// <c>ctx-size</c> lookup, and the append-only
/// <see cref="UserModelOverrides.EnsureSection"/>. Every test runs against
/// its own temp path — no shared static state.
/// </summary>
public sealed class UserModelOverridesTests : IDisposable
{
    /// <summary>
    /// The template as it must land on disk, spelled out independently of
    /// the implementation (byte-identical to the macOS sibling app's seeded
    /// template): LF-only, no BOM, and ending with the last comment's newline
    /// — no trailing blank line (asserted on the bytes below).
    /// </summary>
    private const string ExpectedTemplate =
        "; Custom settings for your models, applied on top of the ones the app picks.\n"
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

    private readonly string _dir;
    private readonly string _path;

    public UserModelOverridesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "llama-umo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, UserModelOverrides.FileName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private UserModelOverrides NewOverrides() => new(_path);

    private void WriteUserFile(string content) => File.WriteAllText(_path, content);

    private static string Lf(string text) => text.Replace("\r\n", "\n");

    // ---- Template seeding ----

    [Fact]
    public void Seed_WritesExactTemplateBytes()
    {
        NewOverrides().SeedTemplateIfNeeded();

        var expected = Encoding.UTF8.GetBytes(ExpectedTemplate);
        Assert.Equal(expected, File.ReadAllBytes(_path));
    }

    [Fact]
    public void Seed_TemplateIsUtf8WithoutBom_LfOnly_TerminalNewline()
    {
        NewOverrides().SeedTemplateIfNeeded();

        var bytes = File.ReadAllBytes(_path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "template must not carry a UTF-8 BOM");
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.Equal(ExpectedTemplate, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Seed_TemplateParsesToZeroSections()
    {
        NewOverrides().SeedTemplateIfNeeded();

        Assert.Empty(UserModelOverrides.Parse(ExpectedTemplate));
    }

    [Fact]
    public void Seed_CreatesMissingParentDirectory()
    {
        var nested = Path.Combine(_dir, "a", "b", "models.user.ini");
        var overrides = new UserModelOverrides(nested);

        overrides.SeedTemplateIfNeeded();

        Assert.Equal(UserModelOverrides.TemplateBytes, File.ReadAllBytes(nested));
    }

    [Fact]
    public void Seed_DoesNotOverwriteExistingFile()
    {
        WriteUserFile("[mine]\ntemp = 0.1\n");

        NewOverrides().SeedTemplateIfNeeded();

        Assert.Equal("[mine]\ntemp = 0.1\n", File.ReadAllText(_path));
    }

    [Fact]
    public void Seed_RecreatesTemplateAfterDeletion()
    {
        var overrides = NewOverrides();
        overrides.SeedTemplateIfNeeded();
        File.Delete(_path);

        overrides.SeedTemplateIfNeeded();

        Assert.Equal(UserModelOverrides.TemplateBytes, File.ReadAllBytes(_path));
    }

    [Fact]
    public void Seed_IsIdempotent()
    {
        var overrides = NewOverrides();
        overrides.SeedTemplateIfNeeded();
        var first = File.ReadAllBytes(_path);

        overrides.SeedTemplateIfNeeded();

        Assert.Equal(first, File.ReadAllBytes(_path));
    }

    [Fact]
    public void RacingSeeds_LeaveExactlyOneTemplate()
    {
        var overrides = NewOverrides();
        Parallel.For(0, 8, _ => overrides.SeedTemplateIfNeeded());

        Assert.Equal(Encoding.UTF8.GetBytes(ExpectedTemplate), File.ReadAllBytes(_path));
    }

    // ---- Parsing ----

    [Fact]
    public void Parse_KeepsSectionAndPairOrder_DroppingCommentsAndBlanks()
    {
        var sections = UserModelOverrides.Parse(Lf("""
            ; leading comment

            # hash comment
            orphan-key = ignored

            [org/a:Q4_0]
            temp = 0.7

            ; middle comment
            top-p = 0.8

            [org/b:Q8_0]
            ctx-size = 1024
            """));

        Assert.Equal(new[] { "org/a:Q4_0", "org/b:Q8_0" }, sections.Select(s => s.Name).ToList());
        Assert.Equal(
            new[] { ("temp", "0.7"), ("top-p", "0.8") },
            sections[0].Pairs.Select(p => (p.Key, p.Value)).ToList());
        Assert.Equal(
            new[] { ("ctx-size", "1024") },
            sections[1].Pairs.Select(p => (p.Key, p.Value)).ToList());
    }

    [Fact]
    public void Parse_SplitsOnFirstEquals_SoJsonValuesSurvive()
    {
        var sections = UserModelOverrides.Parse(Lf("""
            [org/a:Q4_0]
            chat-template-kwargs = {"stop":["<|eot|>"],"x":"a=b","n":1}
            """));

        Assert.Equal(
            "{\"stop\":[\"<|eot|>\"],\"x\":\"a=b\",\"n\":1}",
            sections[0].Pairs.Single().Value);
    }

    [Fact]
    public void Parse_CoalescesRepeatedSections_IntoFirstOccurrence()
    {
        var sections = UserModelOverrides.Parse(Lf("""
            [org/a:Q4_0]
            temp = 0.7
            [org/b:Q8_0]
            temp = 0.1
            [org/a:Q4_0]
            top-p = 0.8
            temp = 0.9
            """));

        // Coalescing is per name: the repeated `org/a:Q4_0` folds into its
        // first occurrence, while the distinct `org/b:Q8_0` stays its own
        // section — two sections total, in first-occurrence order.
        Assert.Equal(
            ["org/a:Q4_0", "org/b:Q8_0"],
            sections.Select(s => s.Name).ToList());
        // `temp` replaces in place (first position), `top-p` appends — and
        // the b section's keys went to b, not to the coalesced cursor.
        Assert.Equal(
            [("temp", "0.9"), ("top-p", "0.8")],
            sections[0].Pairs.Select(p => (p.Key, p.Value)));
        Assert.Equal(
            [("temp", "0.1")],
            sections[1].Pairs.Select(p => (p.Key, p.Value)));
    }

    [Fact]
    public void Parse_RepeatedKeysKeepTheirLastValue()
    {
        var sections = UserModelOverrides.Parse(Lf("""
            [m]
            ctx-size = 1
            ctx-size = 2
            """));

        Assert.Equal(
            new[] { ("ctx-size", "2") },
            sections[0].Pairs.Select(p => (p.Key, p.Value)).ToList());
    }

    [Fact]
    public void Parse_IgnoresEmptyHeaders_And_HandlesWhitespaceAndCrlf()
    {
        var sections = UserModelOverrides.Parse("[\t]\r\nstray = 1\r\n[]\r\n[  spaced name  ]\r\ntemp = 0.7\r\n");

        var only = Assert.Single(sections);
        Assert.Equal("spaced name", only.Name);
        Assert.Equal(
            new[] { ("temp", "0.7") },
            only.Pairs.Select(p => (p.Key, p.Value)).ToList());
    }

    [Fact]
    public void Parse_ToleratesEmptyValues_And_LinesWithoutSeparators()
    {
        var sections = UserModelOverrides.Parse(Lf("""
            [m]
            bare line
            empty =
            """));

        Assert.Equal(
            new[] { ("empty", "") },
            sections[0].Pairs.Select(p => (p.Key, p.Value)).ToList());
    }

    // ---- Commented headers / inline comments (llama.cpp grammar) ----

    [Fact]
    public void Parse_HeaderWithTrailingComment_IsAHeader_NotAComment()
    {
        var sections = UserModelOverrides.Parse(Lf("""
            [org/a:Q4_0] ; pinned for testing
            temp = 0.7
            [org/b:Q8_0] # second one
            ctx-size = 1024
            [org/c:Q4_0] ;
            top-p = 0.8
            """));

        // If the commented headers were read as comments, `temp` would land
        // on the PREVIOUS section and the following keys would too.
        Assert.Equal(
            new[] { "org/a:Q4_0", "org/b:Q8_0", "org/c:Q4_0" },
            sections.Select(s => s.Name).ToList());
        Assert.Equal(("temp", "0.7"), sections[0].Pairs.Select(p => (p.Key, p.Value)).Single());
        Assert.Equal(("ctx-size", "1024"), sections[1].Pairs.Select(p => (p.Key, p.Value)).Single());
        Assert.Equal(("top-p", "0.8"), sections[2].Pairs.Select(p => (p.Key, p.Value)).Single());
    }

    [Fact]
    public void Parse_HeaderWithTrailingComment_IsFound_ByEnsureSection()
    {
        WriteUserFile("[m] ; mine\ntemp = 0.5\n");

        NewOverrides().EnsureSection("m");

        // Idempotence must see the commented header: no second [m].
        Assert.Equal("[m] ; mine\ntemp = 0.5\n", File.ReadAllText(_path));
    }

    [Fact]
    public void Parse_KeyValuesDropTrailingComments_AndWhitespace()
    {
        var sections = UserModelOverrides.Parse(Lf("""
            [m]
            temp = 0.7 ; hot
            top-p = 0.8 # nucleus
            min-p = 0.05;
            ctx-size = 1024
            """));

        Assert.Equal(
            new[] { ("temp", "0.7"), ("top-p", "0.8"), ("min-p", "0.05"), ("ctx-size", "1024") },
            sections[0].Pairs.Select(p => (p.Key, p.Value)).ToList());
    }

    // ---- Generated-option aliases (canonicalized in both directions) ----

    [Theory]
    [InlineData("c", "ctx-size")]
    [InlineData("ctx-size", "ctx-size")]
    [InlineData("m", "model")]
    [InlineData("model", "model")]
    [InlineData("md", "spec-draft-model")]
    [InlineData("model-draft", "spec-draft-model")]
    [InlineData("spec-draft-model", "spec-draft-model")]
    [InlineData("mm", "mmproj")]
    [InlineData("mmproj", "mmproj")]
    [InlineData("spec-type", "spec-type")]
    [InlineData("LLAMA_ARG_CTX_SIZE", "ctx-size")]
    [InlineData("LLAMA_ARG_MODEL", "model")]
    [InlineData("LLAMA_ARG_MMPROJ", "mmproj")]
    [InlineData("LLAMA_ARG_SPEC_TYPE", "spec-type")]
    [InlineData("LLAMA_ARG_SPEC_DRAFT_MODEL", "spec-draft-model")]
    [InlineData("temp", "temp")] // unknown keys pass through untouched
    public void Parse_AliasKeys_Canonicalize(string written, string expected)
    {
        var sections = UserModelOverrides.Parse($"[m]\n{written} = V\n");

        Assert.Equal((expected, "V"), sections[0].Pairs.Select(p => (p.Key, p.Value)).Single());
    }

    [Theory]
    // ctx-size: {"-c", "--ctx-size"}
    [InlineData("c", "ctx-size", "ctx-size")]
    [InlineData("ctx-size", "c", "ctx-size")]
    [InlineData("c", "c", "ctx-size")]
    [InlineData("ctx-size", "ctx-size", "ctx-size")]
    // model: {"-m", "--model"}
    [InlineData("m", "model", "model")]
    [InlineData("model", "m", "model")]
    [InlineData("m", "m", "model")]
    [InlineData("model", "model", "model")]
    // spec-draft-model: {"--spec-draft-model", "-md", "--model-draft"}
    [InlineData("md", "spec-draft-model", "spec-draft-model")]
    [InlineData("spec-draft-model", "md", "spec-draft-model")]
    [InlineData("model-draft", "spec-draft-model", "spec-draft-model")]
    [InlineData("spec-draft-model", "model-draft", "spec-draft-model")]
    [InlineData("md", "model-draft", "spec-draft-model")]
    // mmproj: {"-mm", "--mmproj"}
    [InlineData("mm", "mmproj", "mmproj")]
    [InlineData("mmproj", "mm", "mmproj")]
    [InlineData("mm", "mm", "mmproj")]
    [InlineData("mmproj", "mmproj", "mmproj")]
    // env spellings, both directions against the generated canonical key
    [InlineData("ctx-size", "LLAMA_ARG_CTX_SIZE", "ctx-size")]
    [InlineData("LLAMA_ARG_CTX_SIZE", "ctx-size", "ctx-size")]
    [InlineData("model", "LLAMA_ARG_MODEL", "model")]
    [InlineData("LLAMA_ARG_MODEL", "model", "model")]
    [InlineData("mmproj", "LLAMA_ARG_MMPROJ", "mmproj")]
    [InlineData("LLAMA_ARG_MMPROJ", "mmproj", "mmproj")]
    [InlineData("spec-type", "LLAMA_ARG_SPEC_TYPE", "spec-type")]
    [InlineData("LLAMA_ARG_SPEC_TYPE", "spec-type", "spec-type")]
    [InlineData("spec-draft-model", "LLAMA_ARG_SPEC_DRAFT_MODEL", "spec-draft-model")]
    [InlineData("LLAMA_ARG_SPEC_DRAFT_MODEL", "spec-draft-model", "spec-draft-model")]
    public void Merge_AliasesOfTheSameOption_MergeAsOneKey_WhicheverSpellingEitherSideWrote(
        string generatedKey, string userKey, string canonical)
    {
        WriteUserFile($"[m]\n{userKey} = USER\n");
        var generated = $"[m]\n{generatedKey} = GEN\ntemp = 0.1\n\n";

        var merged = NewOverrides().ReloadAndApply(generated);

        // User wins — and only ONE key reaches the server, canonically named,
        // not one per spelling.
        Assert.Equal($"[m]\n{canonical} = USER\ntemp = 0.1\n\n", merged);
    }

    [Fact]
    public void Merge_BothSpellingsOfAKey_InOneSection_AreOneKey_LastWins()
    {
        WriteUserFile("[m]\nc = 1\nctx-size = 2\n");
        var generated = "[m]\nctx-size = 4096\nmodel = /m.gguf\n\n";

        var merged = NewOverrides().ReloadAndApply(generated);

        Assert.Equal("[m]\nctx-size = 2\nmodel = /m.gguf\n\n", merged);
    }

    [Fact]
    public void Merge_CanonicalAndEnvSpellingsOfAKey_InOneSection_AreOneKey_LastWins()
    {
        WriteUserFile("[m]\nLLAMA_ARG_CTX_SIZE = 1\nctx-size = 2\nLLAMA_ARG_CTX_SIZE = 3\n");
        var generated = "[m]\nctx-size = 4096\nmodel = /m.gguf\n\n";

        var merged = NewOverrides().ReloadAndApply(generated);

        Assert.Equal("[m]\nctx-size = 3\nmodel = /m.gguf\n\n", merged);
    }

    [Fact]
    public void Merge_UserAlias_OverridesGeneratedKey_AmongOthers()
    {
        // The realistic case: the app generated all keys canonically, the
        // user's file aliases a few of them.
        WriteUserFile(Lf("""
            [m]
            c = 32768
            model-draft = /d.gguf
            mm = /mm.gguf
            """));
        var generated = "[m]\nctx-size = 8192\nspec-draft-model = /old.gguf\nmmproj = /old-mm.gguf\nmodel = /m.gguf\n\n";

        var merged = NewOverrides().ReloadAndApply(generated);

        Assert.Equal(
            "[m]\nctx-size = 32768\nspec-draft-model = /d.gguf\nmmproj = /mm.gguf\nmodel = /m.gguf\n\n",
            merged);
    }

    [Fact]
    public void Merge_AliasedCtxSize_ThenPublish_PinsTheCache()
    {
        WriteUserFile("[m]\nc = 32768\n");
        var overrides = NewOverrides();
        _ = overrides.ReloadAndApply("[m]\nctx-size = 8192\n\n");
        overrides.PublishOverrides();

        Assert.Equal("32768", overrides.OverriddenCtxSize("m"));
    }

    [Fact]
    public void Merge_EnvAliasedCtxSize_ThenPublish_PinsTheCache()
    {
        WriteUserFile("[m]\nLLAMA_ARG_CTX_SIZE = 32768\n");
        var overrides = NewOverrides();
        _ = overrides.ReloadAndApply("[m]\nctx-size = 8192\n\n");
        overrides.PublishOverrides();

        Assert.Equal("32768", overrides.OverriddenCtxSize("m"));
    }

    // ---- Merge ----

    [Fact]
    public void Merge_UserWinsInPlace_PreservesGeneratedKeysAndPositions()
    {
        WriteUserFile(Lf("""
            [org/repo:Q4_0]
            ctx-size = 32768
            temp = 0.7
            """));
        var generated = "[org/repo:Q4_0]\nctx-size = 8192\nspec-type = draft-mtp\nmodel = C:/m.gguf\n\n";

        var merged = NewOverrides().ReloadAndApply(generated);

        Assert.Equal(
            "[org/repo:Q4_0]\nctx-size = 32768\nspec-type = draft-mtp\nmodel = C:/m.gguf\ntemp = 0.7\n\n",
            merged);
    }

    [Fact]
    public void Merge_AppendsUnmatchedSections_InFileOrder_AfterGenerated()
    {
        WriteUserFile(Lf("""
            [zzz/last:UD]
            model = /last.gguf
            [aaa/first:UD]
            model = /first.gguf
            """));
        var generated = "[org/repo:Q4_0]\nctx-size = 8192\n\n";

        var merged = NewOverrides().ReloadAndApply(generated);

        Assert.Equal(
            "[org/repo:Q4_0]\nctx-size = 8192\n\n"
            + "[zzz/last:UD]\nmodel = /last.gguf\n\n"
            + "[aaa/first:UD]\nmodel = /first.gguf\n\n",
            merged);
    }

    [Fact]
    public void Merge_KeepsGeneratedSectionOrder_WhenUserTouchesSeveral()
    {
        WriteUserFile(Lf("""
            [c]
            k = 3
            [a]
            k = 1
            """));
        var generated = "[a]\nk = 0\n\n[b]\nk = 0\n\n[c]\nk = 0\n\n";

        var merged = NewOverrides().ReloadAndApply(generated);

        Assert.Equal("[a]\nk = 1\n\n[b]\nk = 0\n\n[c]\nk = 3\n\n", merged);
    }

    [Fact]
    public void Merge_CarriesJsonValuesWithEquals_AcrossTheRoundTrip()
    {
        WriteUserFile("[m]\nchat-template-kwargs = {\"x\":\"a=b\"}\n");

        var merged = NewOverrides().ReloadAndApply("[m]\ntemp = 0.5\n\n");

        Assert.Equal(
            "[m]\ntemp = 0.5\nchat-template-kwargs = {\"x\":\"a=b\"}\n\n",
            merged);
    }

    [Fact]
    public void Merge_CoalescedDuplicateSections_EffectivelyWinOnce()
    {
        WriteUserFile(Lf("""
            [m]
            ctx-size = 1024
            [m]
            ctx-size = 2048
            """));

        var merged = NewOverrides().ReloadAndApply("[m]\nctx-size = 4096\ntemp = 0.5\n\n");

        Assert.Equal("[m]\nctx-size = 2048\ntemp = 0.5\n\n", merged);
    }

    // ---- Empty (scaffold) sections: on disk, inert everywhere else ----

    [Fact]
    public void EmptyOverrideSection_IsNotMergedAndCreatesNoGhostSection()
    {
        // The exact shape EnsureSection leaves for an unedited scaffold.
        WriteUserFile("[scaffold:Q4_0]\n\n[m]\ntemp = 0.5\n");

        var merged = NewOverrides().ReloadAndApply("[m]\ntemp = 0.1\n\n");

        Assert.Equal("[m]\ntemp = 0.5\n\n", merged);
    }

    [Fact]
    public void EmptyOverrideSection_ForGeneratedModel_DoesNotMerge_ItContributesNothing()
    {
        WriteUserFile("[m]\n");

        var generated = "[m]\ntemp = 0.1\nmodel = C:/m.gguf\n\n";

        Assert.Same(generated, NewOverrides().ReloadAndApply(generated));
    }

    [Fact]
    public void EmptySections_DoNotCountAsActiveOverrides()
    {
        var overrides = NewOverrides();
        WriteUserFile("[a]\n\n[b]\n\n");
        overrides.ReloadAndApply("");

        Assert.False(overrides.HasActiveOverrides);

        File.WriteAllText(_path, "[a]\ntemp = 0.5\n\n[b]\n");
        overrides.ReloadAndApply("");

        Assert.True(overrides.HasActiveOverrides);
    }

    [Fact]
    public void EmptyScaffold_SurvivesAReload_Untouched()
    {
        WriteUserFile("[m]\n");
        var overrides = NewOverrides();
        _ = overrides.ReloadAndApply("");

        // Reload never rewrites the user file; scaffolds persist on disk.
        Assert.Equal("[m]\n", File.ReadAllText(_path));
    }

    [Theory]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    [InlineData("utf16-le-bom")]
    [InlineData("utf16-be-bom")]
    [InlineData("utf32-le")]
    [InlineData("utf32-be")]
    public void EnsureSection_NonUtf8Files_AreRejected_WithoutAnyMutation(string encoding)
    {
        const string content = "[m]\ntemp = 0.5\n";
        byte[] body = encoding switch
        {
            "utf16-le" => Encoding.Unicode.GetBytes(content),
            "utf16-be" => Encoding.BigEndianUnicode.GetBytes(content),
            "utf16-le-bom" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(content)],
            "utf16-be-bom" => [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(content)],
            "utf32-le" => Encoding.UTF32.GetBytes(content),
            _ => new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetBytes(content),
        };
        File.WriteAllBytes(_path, body);
        var before = File.ReadAllBytes(_path);

        // Rejected before any write — for a scaffolded section and a new one.
        Assert.Throws<InvalidDataException>(() => NewOverrides().EnsureSection("m"));
        Assert.Throws<InvalidDataException>(() => NewOverrides().EnsureSection("other"));
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    public void EnsureSection_Utf8Files_WithOrWithoutBom_AreAppendedNormally(string encoding)
    {
        const string content = "[m]\ntemp = 0.5\n";
        byte[] body = encoding == "utf8-bom"
            ? [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(content)]
            : Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(_path, body);

        // Idempotence sees through the BOM: the existing section is found.
        NewOverrides().EnsureSection("m");
        Assert.Equal(body, File.ReadAllBytes(_path));

        NewOverrides().EnsureSection("other");
        byte[] expected = [.. body, .. Encoding.UTF8.GetBytes("[other]\n\n")];
        Assert.Equal(expected, File.ReadAllBytes(_path));
    }

    // ---- No-op: legacy bytes come back exactly ----

    [Fact]
    public void NoUserFile_ReturnsGeneratedStringUntouched()
    {
        var generated = "[org/repo:Q4_0]\nctx-size = 8192\n\n[a]\nk = v\n\n";

        // The very same string, not a re-serialization.
        Assert.Same(generated, NewOverrides().ReloadAndApply(generated));
    }

    [Fact]
    public void CommentOnlyUserFile_ReturnsGeneratedStringUntouched()
    {
        WriteUserFile(ExpectedTemplate);
        var generated = "[m]\nctx-size = 1\n\n";

        Assert.Same(generated, NewOverrides().ReloadAndApply(generated));
    }

    [Fact]
    public void EmptyUserFile_ReturnsGeneratedStringUntouched()
    {
        WriteUserFile("");
        var generated = "[m]\nctx-size = 1\n\n";

        Assert.Same(generated, NewOverrides().ReloadAndApply(generated));
    }

    [Fact]
    public void NoActiveOverrides_Reports_NoActiveOverrides()
    {
        var overrides = NewOverrides();
        Assert.False(overrides.HasActiveOverrides);

        WriteUserFile("; nothing\n");
        var generated = "[m]\nctx-size = 1\n\n";
        Assert.Same(generated, overrides.ReloadAndApply(generated));
        Assert.False(overrides.HasActiveOverrides);

        WriteUserFile("[m]\ntemp = 0.5\n");
        overrides.ReloadAndApply("");
        Assert.True(overrides.HasActiveOverrides);
    }

    // ---- Suspension / re-enable ----

    [Fact]
    public void Suspended_OverridesAreIgnored_And_GeneratedBytesComeBackUntouched()
    {
        WriteUserFile("[m]\ntemp = 0.5\n");
        var overrides = NewOverrides();
        var generated = "[m]\ntemp = 0.1\n\n";
        Assert.NotEqual(generated, overrides.ReloadAndApply(generated));

        overrides.Suspend("temp");

        Assert.Equal("temp", overrides.SuspendedOption);
        Assert.True(overrides.IsSuspended);
        Assert.False(overrides.HasActiveOverrides);
        Assert.Same(generated, overrides.ReloadAndApply(generated));
    }

    [Fact]
    public void Suspension_Holds_WhileFileContentIsUnchanged()
    {
        WriteUserFile("[m]\ntemp = 0.5\n");
        var overrides = NewOverrides();
        var generated = "[m]\ntemp = 0.1\n\n";
        _ = overrides.ReloadAndApply(generated);
        overrides.Suspend("temp");

        // Same bytes re-read: the suspension is about this content.
        Assert.Same(generated, overrides.ReloadAndApply(generated));
        Assert.True(overrides.IsSuspended);
    }

    [Fact]
    public void EditingTheFile_ReEnablesOverrides()
    {
        WriteUserFile("[m]\ntemp = 0.5\n");
        var overrides = NewOverrides();
        var generated = "[m]\ntemp = 0.1\n\n";
        _ = overrides.ReloadAndApply(generated);
        overrides.Suspend("temp");

        WriteUserFile("[m]\ntemp = 0.6\n");
        var merged = overrides.ReloadAndApply(generated);

        Assert.False(overrides.IsSuspended);
        Assert.Null(overrides.SuspendedOption);
        Assert.Equal("[m]\ntemp = 0.6\n\n", merged);
    }

    [Fact]
    public void DeletingTheFile_ReEnablesOverrides_WithNoOverridesLeft()
    {
        WriteUserFile("[m]\ntemp = 0.5\n");
        var overrides = NewOverrides();
        var generated = "[m]\ntemp = 0.1\n\n";
        _ = overrides.ReloadAndApply(generated);
        overrides.Suspend("temp");

        File.Delete(_path);

        Assert.Same(generated, overrides.ReloadAndApply(generated));
        Assert.False(overrides.IsSuspended);
    }

    // ---- Cached OverriddenCtxSize ----

    [Fact]
    public void OverriddenCtxSize_IsNull_WithoutOverride_And_WhileSuspended()
    {
        var overrides = NewOverrides();
        _ = overrides.ReloadAndApply("");
        overrides.PublishOverrides();
        Assert.Null(overrides.OverriddenCtxSize("m"));

        WriteUserFile("[m]\nctx-size = 32768\ntemp = 0.5\n");
        _ = overrides.ReloadAndApply("");
        overrides.PublishOverrides();
        Assert.Equal("32768", overrides.OverriddenCtxSize("m"));
        Assert.Null(overrides.OverriddenCtxSize("other"));

        overrides.Suspend("ctx-size");
        Assert.Null(overrides.OverriddenCtxSize("m"));
    }

    [Fact]
    public void OverriddenCtxSize_IsEffective_AcrossDuplicateSections()
    {
        WriteUserFile(Lf("""
            [m]
            ctx-size = 1024
            [m]
            ctx-size = 2048
            """));
        var overrides = NewOverrides();
        _ = overrides.ReloadAndApply("");
        overrides.PublishOverrides();

        Assert.Equal("2048", overrides.OverriddenCtxSize("m"));
    }

    [Fact]
    public void OverriddenCtxSize_TracksFileEdits_OnPublish()
    {
        WriteUserFile("[m]\nctx-size = 1024\n");
        var overrides = NewOverrides();
        _ = overrides.ReloadAndApply("");
        overrides.PublishOverrides();
        Assert.Equal("1024", overrides.OverriddenCtxSize("m"));

        WriteUserFile("[m]\ntemp = 0.5\n");
        _ = overrides.ReloadAndApply("");
        // Reload stages the new content but publishes nothing: the cache
        // keeps answering for the configuration the server accepted.
        Assert.Equal("1024", overrides.OverriddenCtxSize("m"));

        overrides.PublishOverrides();
        Assert.Null(overrides.OverriddenCtxSize("m"));
    }

    [Fact]
    public void OverriddenCtxSize_UsesTheSameValueTheMergeApplies()
    {
        WriteUserFile(Lf("""
            [m]
            temp = 0.5
            ctx-size = 1024
            [m]
            ctx-size = 2048
            """));
        var overrides = NewOverrides();
        var merged = overrides.ReloadAndApply("[m]\nctx-size = 4096\nmodel = C:/m.gguf\n\n");
        overrides.PublishOverrides();

        Assert.Equal("2048", overrides.OverriddenCtxSize("m"));
        Assert.Equal("[m]\nctx-size = 2048\nmodel = C:/m.gguf\ntemp = 0.5\n\n", merged);
    }

    // ---- Generation / staged publication / conditional suspension ----

    [Fact]
    public void Generation_AdvancesOnlyOnContentChange()
    {
        var overrides = NewOverrides();
        Assert.Equal(0L, overrides.Generation);

        WriteUserFile("[m]\ntemp = 0.5\n");
        overrides.ReloadAndApply("");
        Assert.Equal(1L, overrides.Generation);

        // Unchanged re-read (including absent→absent above) keeps the count.
        overrides.ReloadAndApply("");
        Assert.Equal(1L, overrides.Generation);

        File.Delete(_path);
        overrides.ReloadAndApply("");
        Assert.Equal(2L, overrides.Generation);
    }

    [Fact]
    public void Publish_RebuildsCtxCache_FromTheStagedSections()
    {
        WriteUserFile("[m]\nc = 32768\n");
        var overrides = NewOverrides();
        Assert.Null(overrides.OverriddenCtxSize("m")); // nothing published yet

        _ = overrides.ReloadAndApply("");
        Assert.Null(overrides.OverriddenCtxSize("m")); // staged, not accepted

        overrides.PublishOverrides();
        Assert.Equal("32768", overrides.OverriddenCtxSize("m"));
    }

    [Fact]
    public void Publish_IsIdempotent_And_UnpublishedReloadKeepsTheAcceptedValues()
    {
        WriteUserFile("[m]\nctx-size = 1024\n");
        var overrides = NewOverrides();
        _ = overrides.ReloadAndApply("");
        overrides.PublishOverrides();
        overrides.PublishOverrides();

        WriteUserFile("[m]\nctx-size = 2048\n");
        _ = overrides.ReloadAndApply("");

        // The server still runs the accepted 1024 configuration; only a
        // publish may move the cache.
        Assert.Equal("1024", overrides.OverriddenCtxSize("m"));

        overrides.PublishOverrides();
        Assert.Equal("2048", overrides.OverriddenCtxSize("m"));
    }

    [Fact]
    public void Publish_WhileSuspended_DoesNotResurrectOverrides()
    {
        WriteUserFile("[m]\nctx-size = 32768\n");
        var overrides = NewOverrides();
        _ = overrides.ReloadAndApply("");
        overrides.PublishOverrides();
        Assert.Equal("32768", overrides.OverriddenCtxSize("m"));

        overrides.Suspend("ctx-size");
        overrides.PublishOverrides();

        Assert.Null(overrides.OverriddenCtxSize("m"));
    }

    [Fact]
    public void Publish_WithTheCapturedGeneration_TakesEffect()
    {
        WriteUserFile("[m]\nctx-size = 32768\n");
        var overrides = NewOverrides();
        _ = overrides.ReloadAndApply("");
        var staged = overrides.Generation; // what the writer records

        overrides.PublishOverrides(staged);

        Assert.Equal("32768", overrides.OverriddenCtxSize("m"));
    }

    [Fact]
    public void Publish_WithStaleGeneration_IsANoOp_AndCannotPublishNewerContent()
    {
        WriteUserFile("[m]\nctx-size = 1024\n");
        var overrides = NewOverrides();
        _ = overrides.ReloadAndApply("");
        overrides.PublishOverrides();
        var accepted = overrides.Generation;

        // The user edits the file before the acceptance lands: the newer
        // content was never handed to the server, and a stale publish must
        // not pin it.
        WriteUserFile("[m]\nctx-size = 2048\n");
        _ = overrides.ReloadAndApply("");

        overrides.PublishOverrides(accepted);

        Assert.Equal("1024", overrides.OverriddenCtxSize("m")); // still accepted content

        // The current generation publishes the newer content, as usual.
        overrides.PublishOverrides(overrides.Generation);
        Assert.Equal("2048", overrides.OverriddenCtxSize("m"));
    }

    [Fact]
    public void Publish_WithGenerationMismatch_IsANoOp_WhenNothingWasPublishedYet()
    {
        WriteUserFile("[m]\nctx-size = 32768\n");
        var overrides = NewOverrides();
        _ = overrides.ReloadAndApply("");
        Assert.Equal(1L, overrides.Generation);

        overrides.PublishOverrides(expectedGeneration: 0); // mismatch

        Assert.Null(overrides.OverriddenCtxSize("m"));
    }

    // ---- Published-state invalidation (server identity changes) ----

    [Fact]
    public void InvalidatePublishedOverrides_ClearsCache_AndAllowsLaterRepublish()
    {
        WriteUserFile("[m]\nctx-size = 32768\n");
        var overrides = NewOverrides();
        _ = overrides.ReloadAndApply("");
        var staged = overrides.Generation;
        overrides.PublishOverrides(staged);
        Assert.Equal("32768", overrides.OverriddenCtxSize("m"));

        overrides.InvalidatePublishedOverrides();

        Assert.Null(overrides.OverriddenCtxSize("m")); // effective cache cleared

        // The staged configuration is still intact: a publish on an accepted
        // configuration brings the same values right back.
        overrides.PublishOverrides(staged);
        Assert.Equal("32768", overrides.OverriddenCtxSize("m"));
    }

    [Fact]
    public void InvalidatePublishedOverrides_KeepsSectionsGenerationAndSuspension()
    {
        WriteUserFile("[m]\nctx-size = 32768\n");
        var overrides = NewOverrides();
        var merged = overrides.ReloadAndApply("[m]\nmodel = C:/m.gguf\n\n");
        var generation = overrides.Generation;
        overrides.PublishOverrides(generation);

        overrides.InvalidatePublishedOverrides();

        // Only the published state is cleared: the raw sections still merge,
        // the generation is untouched, and a suspension still holds.
        Assert.Equal(generation, overrides.Generation);
        Assert.Equal(merged, overrides.ReloadAndApply("[m]\nmodel = C:/m.gguf\n\n"));
        Assert.Null(overrides.SuspendedOption);
    }

    [Fact]
    public void InvalidatePublishedOverrides_WhileSuspended_KeepsTheSuspension()
    {
        WriteUserFile("[m]\nctx-size = 32768\n");
        var overrides = NewOverrides();
        _ = overrides.ReloadAndApply("");
        overrides.PublishOverrides();
        overrides.Suspend("ctx-size");

        overrides.InvalidatePublishedOverrides();

        Assert.True(overrides.IsSuspended);
        Assert.Equal("ctx-size", overrides.SuspendedOption);

        // And the suspended cache still cannot be resurrected by a publish.
        overrides.PublishOverrides(overrides.Generation);
        Assert.Null(overrides.OverriddenCtxSize("m"));
    }

    [Fact]
    public void Suspend_WithCurrentGeneration_TakesEffect_And_ClearsPublishedCache()
    {
        WriteUserFile("[m]\nctx-size = 32768\n");
        var overrides = NewOverrides();
        _ = overrides.ReloadAndApply("");
        overrides.PublishOverrides();
        var generation = overrides.Generation;

        Assert.True(overrides.Suspend("temp", generation));

        Assert.True(overrides.IsSuspended);
        Assert.Equal("temp", overrides.SuspendedOption);
        Assert.Null(overrides.OverriddenCtxSize("m")); // cleared immediately
    }

    [Fact]
    public void Suspend_WithStaleGeneration_IsANoOp()
    {
        WriteUserFile("[m]\nctx-size = 32768\n");
        var overrides = NewOverrides();
        _ = overrides.ReloadAndApply("");
        overrides.PublishOverrides();
        var generationBeforeEdit = overrides.Generation;

        // The user fixes (or just touches) the file before recovery runs:
        // the rejection belongs to content that is already gone.
        WriteUserFile("[m]\nctx-size = 4096\n");
        _ = overrides.ReloadAndApply("");

        Assert.False(overrides.Suspend("temp", generationBeforeEdit));

        Assert.False(overrides.IsSuspended);
        Assert.Null(overrides.SuspendedOption);
    }

    [Fact]
    public void Suspend_Unconditional_EvenAfterEdits()
    {
        WriteUserFile("[m]\ntemp = 0.5\n");
        var overrides = NewOverrides();
        _ = overrides.ReloadAndApply("");

        overrides.Suspend("temp"); // legacy overload: unconditional

        Assert.True(overrides.IsSuspended);
    }

    // ---- EnsureSection ----

    [Fact]
    public void EnsureSection_MissingFile_SeedsTemplateThenAppendsSection()
    {
        var overrides = NewOverrides();

        var returned = overrides.EnsureSection("org/repo:Q4_0");

        Assert.Equal(_path, returned);
        var expected = ExpectedTemplate + "[org/repo:Q4_0]\n\n";
        Assert.Equal(expected, File.ReadAllText(_path));
    }

    [Fact]
    public void EnsureSection_ExistingFile_AppendsAfterContent_PreservingEveryByte()
    {
        WriteUserFile("[a]\nk = 1\n\n; trailing note\n");

        NewOverrides().EnsureSection("b");

        Assert.Equal("[a]\nk = 1\n\n; trailing note\n[b]\n\n", File.ReadAllText(_path));
    }

    [Fact]
    public void EnsureSection_TerminatesAnUnterminatedLastLine_BeforeTheHeader()
    {
        WriteUserFile("[a]\nk = 1");

        NewOverrides().EnsureSection("b");

        Assert.Equal("[a]\nk = 1\n[b]\n\n", File.ReadAllText(_path));
    }

    [Fact]
    public void EnsureSection_TerminatesATrailingCommentLine_BeforeTheHeader()
    {
        WriteUserFile("; note without newline");

        NewOverrides().EnsureSection("m");

        Assert.Equal("; note without newline\n[m]\n\n", File.ReadAllText(_path));
    }

    [Fact]
    public void EnsureSection_CommentedHeaderDoesNotCount_AsAnExistingSection()
    {
        WriteUserFile(";   [m]\n;   temp = 0.7\n");

        NewOverrides().EnsureSection("m");

        Assert.Equal(";   [m]\n;   temp = 0.7\n[m]\n\n", File.ReadAllText(_path));
    }

    [Fact]
    public void EnsureSection_IsIdempotent()
    {
        WriteUserFile("[a]\nk = 1\n\n");
        var overrides = NewOverrides();

        overrides.EnsureSection("b");
        var afterFirst = File.ReadAllBytes(_path);
        overrides.EnsureSection("b");
        overrides.EnsureSection("b");

        Assert.Equal(afterFirst, File.ReadAllBytes(_path));
        Assert.Equal(1, File.ReadAllText(_path).Split("[b]").Length - 1);
    }

    [Fact]
    public void EnsureSection_SeesRepeatedSectionHeaders_AsPresent()
    {
        WriteUserFile("[m]\nk = 1\n[m]\nk = 2\n");
        var before = File.ReadAllBytes(_path);

        NewOverrides().EnsureSection("m");

        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void EnsureSection_CreatesMissingParentDirectory()
    {
        var nested = Path.Combine(_dir, "x", "y", "models.user.ini");
        var overrides = new UserModelOverrides(nested);

        overrides.EnsureSection("m");

        Assert.Equal(ExpectedTemplate + "[m]\n\n", File.ReadAllText(nested));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a]b")]
    [InlineData("a[b")]
    [InlineData("]")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    public void EnsureSection_UnsafeIds_Throw_BeforeAnyIo(string modelId)
    {
        var overrides = NewOverrides();

        Assert.Throws<ArgumentException>(() => overrides.EnsureSection(modelId));

        // Nothing was created or touched.
        Assert.False(File.Exists(_path));

        WriteUserFile("[keep]\nk = 1\n");
        Assert.Throws<ArgumentException>(() => overrides.EnsureSection(modelId));
        Assert.Equal("[keep]\nk = 1\n", File.ReadAllText(_path));
    }

    // ---- Renderer integration ----

    [Fact]
    public void RenderedPreset_WithUserOverrides_WinsPerKey_And_PreservesModelPaths()
    {
        // One model carrying every app-generated key (ctx, spec type, and the
        // enrolled model/mmproj/draft paths), so the expected output is an
        // exact string.
        const string id = "ggml-org/gemma-4-E4B-it-GGUF:Q8_0";
        var rendered = LlamaManager.RenderModelsPresetIni(
            [new KeyValuePair<string, int>(id, 8192)],
            [new KeyValuePair<string, string>(id, "draft-mtp")],
            [new EnrolledFolderModel(id, "C:/m.gguf", "C:/mmproj.gguf", "C:/draft.gguf")]);
        Assert.Equal(
            $"[{id}]\nctx-size = 8192\nspec-type = draft-mtp\n" +
            "model = C:/m.gguf\nmmproj = C:/mmproj.gguf\nspec-draft-model = C:/draft.gguf\n\n",
            rendered);

        WriteUserFile(Lf($$"""
            [{{id}}]
            ctx-size = 32768
            temp = 0.7
            [unsloth/DeepSeek-V4-Flash-0731-GGUF:UD-Q2_K_XL]
            model = /other.gguf
            spec-type = draft-dspark
            spec-draft-model = /draft.gguf
            """));

        var written = NewOverrides().ReloadAndApply(rendered);

        Assert.Equal(
            $"[{id}]\nctx-size = 32768\nspec-type = draft-mtp\n" +
            "model = C:/m.gguf\nmmproj = C:/mmproj.gguf\nspec-draft-model = C:/draft.gguf\ntemp = 0.7\n\n" +
            "[unsloth/DeepSeek-V4-Flash-0731-GGUF:UD-Q2_K_XL]\n" +
            "model = /other.gguf\nspec-type = draft-dspark\nspec-draft-model = /draft.gguf\n\n",
            written);
    }

    [Fact]
    public void RenderedPreset_WithoutUserFile_IsWrittenByteForByte()
    {
        var rendered = LlamaManager.RenderModelsPresetIni(
            [new KeyValuePair<string, int>("a:Q4_0", 4096), new KeyValuePair<string, int>("b:Q8_0", 8192)],
            [new KeyValuePair<string, string>("b:Q8_0", "draft-dflash")],
            [new EnrolledFolderModel("c-UD", "C:/c.gguf", null, null)]);

        Assert.Same(rendered, NewOverrides().ReloadAndApply(rendered));
    }
}
