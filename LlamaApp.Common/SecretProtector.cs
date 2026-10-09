using System.Security.Cryptography;
using System.Text;

namespace LlamaApp.Common;

/// <summary>
/// One-way helper around Windows DPAPI for secrets persisted in
/// <c>settings.json</c> (currently the Hugging Face access token). The token
/// grants the user's HF private/gated-repo access, and a plaintext per-user
/// file is readable by every process running as that user — DPAPI's
/// <c>CurrentUser</c> scope binds the blob to this user on this machine, so a
/// copied/stolen settings file no longer carries a working credential.
///
/// <para>Best-effort by design: anything unprotectable (a file from another
/// machine/user, corruption, or a missing DPAPI on a non-Windows host) reads
/// back as empty and the UI simply treats the token as unset.</para>
/// </summary>
public static class SecretProtector
{
    /// <summary>
    /// Encrypts <paramref name="plain"/> for the current user (DPAPI,
    /// <c>CurrentUser</c> scope) and returns it as base64. Empty input yields
    /// the empty string — an unset secret stays unset on disk.
    /// </summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain), optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>
    /// Decrypts a <see cref="Protect"/> blob. Empty input yields the empty
    /// string; any failure (wrong machine/user, corruption, non-Windows) also
    /// yields the empty string rather than throwing — the caller treats the
    /// secret as unset.
    /// </summary>
    public static string Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return "";
        try
        {
            var decrypted = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedBase64), optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return "";
        }
    }
}
