using System.Text.Json;

namespace LlamaApp.Views;

/// <summary>
/// Builds the JavaScript that places a shared prompt into the llama WebUI's
/// chat composer. Pure string building so the escaping is unit-testable.
///
/// <para><b>Why a script instead of an API:</b> the WebUI is the llama
/// server's own page — the only contract we can rely on is its DOM: the chat
/// composer is a plain <c>&lt;textarea&gt;</c> (bound by Svelte, which
/// re-synchronizes on <c>input</c> events). The script targets the composer
/// by placeholder (<c>Ask anything...</c>) with a fallback to the last
/// textarea on the page, sets its value through the native setter, and
/// dispatches an <c>input</c> event so the framework's binding updates.
/// Returns plain <c>true</c> when a composer was found and filled — the
/// overlay retries until then, so a share that lands before a model is
/// loaded still delivers once the chat screen is up.</para>
/// </summary>
public static class OverlayPromptInjection
{
    /// <summary>
    /// The composer's placeholder text in the llama WebUI
    /// (<c>ChatFormInputBasic.svelte</c>). Used to pick the right textarea
    /// when more than one exists.
    /// </summary>
    internal const string ComposerPlaceholder = "Ask anything...";

    /// <summary>
    /// The script executed in the WebUI to fill the composer with
    /// <paramref name="prompt"/>. The prompt is JSON-embedded (never
    /// interpolated) so quotes, backslashes, newlines and
    /// <c>&lt;/textarea&gt;</c>-style payloads can't break out. Returns plain
    /// <c>true</c>/<c>false</c> — <c>ExecuteScriptAsync</c> JSON-encodes the
    /// script's result, so a boolean keeps the success check simple.
    /// Idempotent-safe: re-running it replaces the composer's value.
    /// </summary>
    public static string Build(string prompt)
    {
        var json = JsonSerializer.Serialize(prompt);
        return $$"""
            (function () {
                const prompt = {{json}};
                const textareas = Array.from(document.querySelectorAll('textarea'));
                if (textareas.length === 0) return false;
                const composer =
                    textareas.find(t => t.placeholder === {{JsonSerializer.Serialize(ComposerPlaceholder)}}) ||
                    textareas[textareas.length - 1];
                const setter = Object.getOwnPropertyDescriptor(
                    window.HTMLTextAreaElement.prototype, 'value').set;
                setter.call(composer, prompt);
                composer.dispatchEvent(new Event('input', { bubbles: true }));
                return true;
            })();
            """;
    }

    /// <summary>
    /// The script executed in the WebUI to focus the chat composer after the
    /// overlay opens (or a share lands in it), so the user can type right
    /// away instead of clicking the input first. Same composer selection as
    /// <see cref="Build"/>; places the caret at the end of the text and
    /// returns plain <see langword="true"/> when the composer ended up
    /// focused — the same JSON-encoded success contract.
    /// </summary>
    public static string BuildComposerFocus()
    {
        return $$"""
            (function () {
                const textareas = Array.from(document.querySelectorAll('textarea'));
                if (textareas.length === 0) return false;
                const composer =
                    textareas.find(t => t.placeholder === {{JsonSerializer.Serialize(ComposerPlaceholder)}}) ||
                    textareas[textareas.length - 1];
                composer.focus();
                composer.setSelectionRange(composer.value.length, composer.value.length);
                return document.activeElement === composer;
            })();
            """;
    }

    /// <summary>
    /// The script executed in the WebUI to attach a shared image to the chat.
    /// The composer's <c>onPaste</c> handler feeds pasted files into the
    /// attachment pipeline (<c>clipboardData.items → getAsFile →
    /// onFilesAdd</c>), so the image rides a synthetic paste: the base64
    /// payload becomes a <c>File</c> on a constructed <c>DataTransfer</c>,
    /// dispatched on the composer as a <c>ClipboardEvent</c>. The payload is
    /// JSON-embedded (never interpolated). Returns plain <c>true</c> when a
    /// composer was found and the paste dispatched — the same JSON-encoded
    /// success contract as <see cref="Build"/>.
    /// </summary>
    public static string BuildImagePaste(string base64, string mimeType, string fileName)
    {
        return $$"""
            (function () {
                const b64 = {{JsonSerializer.Serialize(base64)}};
                const mime = {{JsonSerializer.Serialize(mimeType)}};
                const name = {{JsonSerializer.Serialize(fileName)}};
                const textareas = Array.from(document.querySelectorAll('textarea'));
                if (textareas.length === 0) return false;
                const composer =
                    textareas.find(t => t.placeholder === {{JsonSerializer.Serialize(ComposerPlaceholder)}}) ||
                    textareas[textareas.length - 1];
                const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
                const file = new File([bytes], name, { type: mime });
                const data = new DataTransfer();
                data.items.add(file);
                composer.dispatchEvent(new ClipboardEvent('paste', {
                    clipboardData: data, bubbles: true, cancelable: true }));
                return true;
            })();
            """;
    }
}
