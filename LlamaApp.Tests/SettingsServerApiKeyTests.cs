using System.Text.Json;
using LlamaApp.Common;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Tests for the persisted server API-key setting: empty by default (the
/// loopback bind needs no key), never serialized as plaintext — the live
/// property is excluded and <see cref="Settings.Save"/> writes only the
/// DPAPI-protected form — and a settings.json from before the protection
/// shipped migrates on load. The key is a LAN access token for the model
/// server, kept behind the same door as the Hugging Face token.
/// </summary>
public sealed class SettingsServerApiKeyTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Fact]
    public void ServerApiKey_DefaultsToEmpty()
    {
        var settings = new Settings();

        Assert.Equal("", settings.ServerApiKey);
    }

    [Fact]
    public void ServerApiKey_MissingFromSavedJson_DefaultsToEmpty()
    {
        const string json = """{"ServerPort":9931,"ListenAddress":"127.0.0.1"}""";

        var settings = JsonSerializer.Deserialize<Settings>(json);

        Assert.NotNull(settings);
        Assert.Equal("", settings.ServerApiKey);
    }

    [Fact]
    public void ServerApiKey_IsNeverSerializedAsPlaintext()
    {
        var settings = new Settings { ServerApiKey = ServerAuth.GenerateApiKey() };

        var json = JsonSerializer.Serialize(settings, Indented);

        Assert.DoesNotContain(settings.ServerApiKey, json);
        Assert.Contains("ProtectedServerApiKey", json);
    }

    [Fact]
    public void ServerApiKey_RoundTripsThroughTheProtectedForm()
    {
        // Save() writes SecretProtector.Protect(ServerApiKey) into the
        // serialized blob; RestoreSecrets reverses it. (Save itself is not
        // exercised — it writes the real per-user settings.json — but it only
        // routes through SecretProtector, whose DPAPI round-trip has its own
        // tests.)
        var key = ServerAuth.GenerateApiKey();
        var json = JsonSerializer.Serialize(
            new { ProtectedServerApiKey = SecretProtector.Protect(key) }, Indented);

        var settings = new Settings();
        Settings.RestoreSecrets(settings, json);

        Assert.Equal(key, settings.ServerApiKey);
    }

    [Fact]
    public void RestoreSecrets_MigratesALegacyPlaintextKey()
    {
        // A settings.json from before the key was protected.
        const string legacyJson =
            """{"ServerPort":9931,"ServerApiKey":"sk_legacy_value"}""";

        var settings = new Settings();
        Settings.RestoreSecrets(settings, legacyJson);

        Assert.Equal("sk_legacy_value", settings.ServerApiKey);
        Assert.Equal("", settings.ProtectedServerApiKey);

        // And the next serialize carries no plaintext (the property is
        // [JsonIgnore]): the protected form is what a Save writes.
        Assert.DoesNotContain("sk_legacy_value", JsonSerializer.Serialize(settings, Indented));
    }

    [Fact]
    public void RestoreSecrets_PrefersTheProtectedBlob()
    {
        var blob = SecretProtector.Protect("sk_protected_value");
        var json = JsonSerializer.Serialize(
            new { ServerApiKey = "sk_legacy_value", ProtectedServerApiKey = blob });

        var settings = new Settings();
        Settings.RestoreSecrets(settings, json);

        Assert.Equal("sk_protected_value", settings.ServerApiKey);
    }

    [Fact]
    public void RestoreSecrets_UndecryptableBlob_TreatsKeyAsUnset()
    {
        // (e.g. a settings.json copied from another machine/user — the app
        // generates a fresh key for the next non-loopback bind.)
        var json = JsonSerializer.Serialize(new { ProtectedServerApiKey = "AAAA" });

        var settings = new Settings();
        Settings.RestoreSecrets(settings, json);

        Assert.Equal("", settings.ServerApiKey);
    }
}
