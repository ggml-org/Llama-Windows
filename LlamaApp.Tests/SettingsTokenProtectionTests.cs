using System.Text.Json;
using LlamaApp.Common;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Tests that the Hugging Face token never reaches settings.json as plaintext:
/// the live property is excluded from serialization, <see cref="Settings.Save"/>
/// writes only the DPAPI-protected form, and a legacy plaintext file migrates
/// on load (and is rewritten protected on the next save).
/// </summary>
public sealed class SettingsTokenProtectionTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Fact]
    public void Token_IsNeverSerializedAsPlaintext()
    {
        var settings = new Settings { HuggingFaceToken = "hf_secret_value" };

        var json = JsonSerializer.Serialize(settings, Indented);

        Assert.DoesNotContain("hf_secret_value", json);
        Assert.Contains("ProtectedHuggingFaceToken", json);
    }

    // (Save() itself is not exercised here — it writes the real per-user
    // settings.json — but it only routes through SecretProtector.Protect,
    // whose DPAPI round-trip has its own tests.)

    [Fact]
    public void RestoreSecrets_MigratesALegacyPlaintextToken()
    {
        // A settings.json from before the token was protected.
        const string legacyJson =
            """{"ServerPort":9931,"HuggingFaceToken":"hf_legacy_value"}""";

        var settings = new Settings();
        Settings.RestoreSecrets(settings, legacyJson);

        Assert.Equal("hf_legacy_value", settings.HuggingFaceToken);
        Assert.Equal("", settings.ProtectedHuggingFaceToken);
    }

    [Fact]
    public void RestoreSecrets_PrefersTheProtectedBlob()
    {
        var blob = SecretProtector.Protect("hf_protected_value");
        var json = JsonSerializer.Serialize(
            new { HuggingFaceToken = "hf_legacy_value", ProtectedHuggingFaceToken = blob });

        var settings = new Settings();
        Settings.RestoreSecrets(settings, json);

        Assert.Equal("hf_protected_value", settings.HuggingFaceToken);
    }

    [Fact]
    public void RestoreSecrets_UndecryptableBlob_TreatsTokenAsUnset()
    {
        var json = JsonSerializer.Serialize(new { ProtectedHuggingFaceToken = "AAAA" });

        var settings = new Settings();
        Settings.RestoreSecrets(settings, json);

        Assert.Equal("", settings.HuggingFaceToken);
    }

    [Fact]
    public void RestoreSecrets_MalformedJson_TreatsTokenAsUnset()
    {
        var settings = new Settings();
        Settings.RestoreSecrets(settings, "{not json");

        Assert.Equal("", settings.HuggingFaceToken);
    }
}
