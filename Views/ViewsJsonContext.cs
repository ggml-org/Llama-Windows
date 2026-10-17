using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace LlamaApp.Views;

/// <summary>
/// Source-generated JSON metadata for the Views' curl sample request body.
/// The old reflection-based overload is annotated trim-unsafe and would
/// break <c>PublishTrimmed</c> builds; output is byte-identical, pinned by
/// <c>ApiRequestPresentationTests</c>. (String embedding for the overlay
/// scripts is also trim-safe — see <c>OverlayPromptInjection.JsonString</c>.)
/// </summary>
[JsonSerializable(typeof(ApiRequestPresentation.CurlRequest), TypeInfoPropertyName = "CurlRequest")]
internal partial class ViewsJsonContext : JsonSerializerContext
{
}
