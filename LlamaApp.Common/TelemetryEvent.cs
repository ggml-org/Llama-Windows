using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlamaApp.Common;

/// <summary>
/// Wire types for the anonymous usage telemetry posted to
/// <see href="https://huggingface.co/api/telemetry-llamacpp"/> (see
/// <c>Telemetry</c> in the app project for the sender). Pure managed types,
/// kept here so tests can pin the exact JSON contract without loading the
/// WinUI app assembly.
/// </summary>
public static class TelemetryJson
{
    /// <summary>
    /// Serializer options shared by sender and tests: the event-type enum
    /// emits lowercase strings ("start" / "ping" / "stop"), mirroring tgi's
    /// serde convention rather than numeric enum values.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string Serialize(TelemetryEvent payload) => JsonSerializer.Serialize(payload, Options);
}

/// <summary>
/// Parses the <c>LLAMA_WINDOWS_TELEMETRY</c> environment variable, the
/// command-line-shaped opt-out that mirrors tgi's telemetry switch:
/// <c>OFF</c> (case-insensitive) disables telemetry for the session,
/// <c>ON</c> keeps it. Anything else — unset, empty, or malformed — returns
/// <c>null</c>: the variable has no opinion, so the default (on) applies
/// through the <c>TelemetryEnabled</c> setting.
/// </summary>
public static class TelemetryEnvVar
{
    public static bool? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Trim().Equals("OFF", StringComparison.OrdinalIgnoreCase)) return false;
        if (value.Trim().Equals("ON", StringComparison.OrdinalIgnoreCase)) return true;
        return null;
    }
}

/// <summary>Lifecycle event kinds, mirroring tgi's <c>EventType</c> (no Error events for now).</summary>
public enum TelemetryEventType
{
    /// <summary>Sent once, when the app process starts.</summary>
    Start,
    /// <summary>Sent every 5 minutes while the process is alive.</summary>
    Ping,
    /// <summary>Sent once, on a clean app exit.</summary>
    Stop,
}

/// <summary>
/// The wire payload — top-level <c>id</c> (per-install GUID) and
/// <c>app</c> (<c>"llamawin"</c>) per the backend contract, plus the event
/// kind, app version, and the reduced hardware snapshot.
/// </summary>
public sealed record TelemetryEvent
{
    /// <summary>Random per-install client id (persisted in settings.json).</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Fixed app tag for the shared telemetry endpoint.</summary>
    [JsonPropertyName("app")]
    public string App { get; init; } = "llamawin";

    /// <summary>Event kind: start, ping, or stop.</summary>
    [JsonPropertyName("event_type")]
    public required TelemetryEventType EventType { get; init; }

    /// <summary>App version (major.minor.patch), when readable.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>Hardware snapshot shared by all event kinds.</summary>
    [JsonPropertyName("env")]
    public required TelemetryEnv Env { get; init; }
}

/// <summary>
/// The reduced environment snapshot: Windows version, CPU, total RAM, and
/// installed GPU names. Deliberately minimal — see <c>Telemetry</c>.
/// </summary>
public sealed record TelemetryEnv
{
    /// <summary>OS family — always <c>"windows"</c> for this app.</summary>
    [JsonPropertyName("os_name")]
    public required string OsName { get; init; }

    /// <summary>Windows version, e.g. <c>10.0.26100</c>.</summary>
    [JsonPropertyName("os_version")]
    public required string OsVersion { get; init; }

    /// <summary>CPU model string; empty when the probe failed.</summary>
    [JsonPropertyName("cpu")]
    public string Cpu { get; init; } = "";

    /// <summary>Logical processor count.</summary>
    [JsonPropertyName("cpu_count")]
    public int CpuCount { get; init; }

    /// <summary>Total physical memory in bytes; 0 when the probe failed.</summary>
    [JsonPropertyName("memory_total")]
    public ulong MemoryTotal { get; init; }

    /// <summary>Installed GPU names; empty array when none or unknown.</summary>
    [JsonPropertyName("gpu")]
    public string[] Gpu { get; init; } = [];
}
