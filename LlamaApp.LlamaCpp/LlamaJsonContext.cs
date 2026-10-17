using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace LlamaApp.Llama;

/// <summary>
/// Source-generated JSON metadata for this assembly's DTOs: the serializer
/// contracts are generated at build time instead of built by runtime
/// reflection on first use — no contract-generation spike on the startup
/// path (the /models poll and the runtime-update check parse through
/// here), less per-parse overhead, and the prerequisite for ever trimming
/// the app (a trimmer can't see reflection-based serialization).
/// </summary>
[JsonSerializable(typeof(LlamaManager.ModelsResponseDto), TypeInfoPropertyName = "ModelsResponseDto")]
[JsonSerializable(typeof(RuntimeUpdateScheduler.LatestReleaseDto), TypeInfoPropertyName = "LatestReleaseDto")]
internal partial class LlamaJsonContext : JsonSerializerContext
{
}
