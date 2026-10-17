using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace LlamaApp.Common;

/// <summary>
/// Source-generated JSON metadata for this assembly's DTOs: the serializer
/// contracts are generated at build time instead of built by runtime
/// reflection on first use — no contract-generation spike on the startup
/// path, less per-parse overhead, and the prerequisite for ever trimming
/// the app (a trimmer can't see reflection-based serialization). Each
/// assembly carries its own context over its own DTO shapes.
/// </summary>
[JsonSerializable(typeof(UpdateChecker.LatestReleaseDto), TypeInfoPropertyName = "LatestReleaseDto")]
internal partial class CommonJsonContext : JsonSerializerContext
{
}
