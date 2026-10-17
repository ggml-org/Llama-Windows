using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace LlamaApp;

/// <summary>
/// Source-generated JSON metadata for <see cref="Settings"/>: the serializer
/// contract is generated at build time instead of built by runtime
/// reflection — cheaper settings round-trips and the prerequisite for ever
/// trimming the app (a trimmer can't see reflection-based serialization).
/// WriteIndented matches the previous hand-shared options used by
/// <see cref="Settings.Save"/>.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Settings), TypeInfoPropertyName = "Settings")]
internal partial class SettingsJsonContext : JsonSerializerContext
{
}
