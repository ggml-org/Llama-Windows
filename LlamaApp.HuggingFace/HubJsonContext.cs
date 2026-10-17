using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace LlamaApp.HuggingFace;

/// <summary>
/// Source-generated JSON metadata for this assembly's DTOs: the serializer
/// contracts are generated at build time instead of built by runtime
/// reflection on first use — no contract-generation spike on the startup
/// path (whoami/catalog/Hub search all parse on launch), less per-parse
/// overhead, and the prerequisite for ever trimming the app. The
/// write-time ignore condition matches the catalog's previous hand-shared
/// options so behavior is unchanged.
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HubClient.HubUserInfoClient.UserInfoDto), TypeInfoPropertyName = "UserInfoDto")]
[JsonSerializable(typeof(HubClient.UserAvatarDto), TypeInfoPropertyName = "UserAvatarDto")]
[JsonSerializable(typeof(List<HubClient.RepoTreeEntryDto>), TypeInfoPropertyName = "RepoTreeEntryList")]
[JsonSerializable(typeof(HubClient.HubModelDto[]), TypeInfoPropertyName = "HubModelArray")]
[JsonSerializable(typeof(Catalog.CatalogFamily[]), TypeInfoPropertyName = "CatalogFamilyArray")]
internal partial class HubJsonContext : JsonSerializerContext
{
}
