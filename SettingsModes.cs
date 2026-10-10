namespace LlamaApp;

/// <summary>Presentation levels. Changing modes never deletes saved tuning.</summary>
public static class SettingsModes
{
    public const string Simple = "simple";
    public const string Advanced = "advanced";
    public const string Expert = "expert";

    public static string Normalize(string? value) => value switch
    {
        Advanced => Advanced,
        Expert => Expert,
        _ => Simple,
    };

    public static bool ShowsAdvanced(string? value) => Normalize(value) != Simple;
    public static bool ShowsExpert(string? value) => Normalize(value) == Expert;

    public static bool HasSavedAdvancedSettings(Settings settings) =>
        settings.ServerPort != Llama.LlamaManager.DefaultServerPort ||
        settings.ListenAddress != Common.ListenAddresses.Localhost ||
        settings.IdleUnloadSeconds != -1 || settings.MaxLoadedModels != 0 ||
        settings.RuntimeBackend != Llama.InferenceRuntime.Automatic ||
        !string.IsNullOrEmpty(settings.GpuDeviceName) || settings.GpuLayers != "auto" ||
        settings.BatchSize != 0 || settings.MicroBatchSize != 0 ||
        settings.FlashAttention != "auto" ||
        settings.CacheTypeK != "f16" || settings.CacheTypeV != "f16" ||
        !string.IsNullOrWhiteSpace(settings.CustomServeArguments) ||
        settings.AdvancedInferenceProfile?.IsAutomatic == false ||
        settings.ModelContextLengths?.Count > 0 ||
        settings.ModelPromptProfiles?.Values.Any(profile => profile?.IsAutomatic == false) == true;
}
