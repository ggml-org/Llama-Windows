using System.Text.Json;
using LlamaApp.Common;
using Xunit;

// Deliberately touches only LlamaApp.Common (pure net10.0) — no WinUI app
// types — so the wire-format contract can be pinned on any architecture.


namespace LlamaApp.Tests;

/// <summary>
/// Wire-format contract for the telemetry payload posted to
/// <c>https://huggingface.co/api/telemetry-llamacpp</c>: the backend parses
/// specific field names, so serialization is pinned here (tgi-style event
/// envelope, reduced desktop env snapshot).
/// </summary>
public class TelemetryEventSerializationTests
{
    private static readonly TelemetryEvent Event = new()
    {
        Id = "0f8fad5b-d9cb-469f-a165-70867728950e",
        EventType = TelemetryEventType.Start,
        Version = "0.12.0",
        Env = new TelemetryEnv
        {
            OsName = "windows",
            OsVersion = "10.0.26100",
            Cpu = "AMD Ryzen 9 7950X",
            CpuCount = 32,
            MemoryTotal = 34359738368UL,
            Gpu = ["NVIDIA GeForce RTX 4060 Ti"],
        },
    };

    [Fact]
    public void Payload_carries_app_tag_llamawin_and_client_id()
    {
        var json = TelemetryJson.Serialize(Event);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("llamawin", doc.RootElement.GetProperty("app").GetString());
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void Event_type_serializes_lowercase_start_ping_stop()
    {
        var start = JsonDocument.Parse(TelemetryJson.Serialize(Event with { EventType = TelemetryEventType.Start }));
        var ping = JsonDocument.Parse(TelemetryJson.Serialize(Event with { EventType = TelemetryEventType.Ping }));
        var stop = JsonDocument.Parse(TelemetryJson.Serialize(Event with { EventType = TelemetryEventType.Stop }));

        Assert.Equal("start", start.RootElement.GetProperty("event_type").GetString());
        Assert.Equal("ping", ping.RootElement.GetProperty("event_type").GetString());
        Assert.Equal("stop", stop.RootElement.GetProperty("event_type").GetString());
    }

    [Fact]
    public void Env_holds_windows_version_cpu_ram_and_gpu_names()
    {
        var json = TelemetryJson.Serialize(Event);
        var env = JsonDocument.Parse(json).RootElement.GetProperty("env");

        Assert.Equal("windows", env.GetProperty("os_name").GetString());
        Assert.Equal("10.0.26100", env.GetProperty("os_version").GetString());
        Assert.Equal("AMD Ryzen 9 7950X", env.GetProperty("cpu").GetString());
        Assert.Equal(32, env.GetProperty("cpu_count").GetInt32());
        Assert.Equal(34359738368UL, env.GetProperty("memory_total").GetUInt64());
        Assert.Equal(["NVIDIA GeForce RTX 4060 Ti"],
            env.GetProperty("gpu").EnumerateArray().Select(g => g.GetString()!).ToArray());
    }

    [Fact]
    public void Gpu_is_empty_array_when_no_gpu_is_installed()
    {
        var json = TelemetryJson.Serialize(Event with
        {
            Env = Event.Env with { Gpu = [] },
        });
        var gpu = JsonDocument.Parse(json).RootElement.GetProperty("env").GetProperty("gpu");

        Assert.Equal(JsonValueKind.Array, gpu.ValueKind);
        Assert.Empty(gpu.EnumerateArray());
    }
}

/// <summary>
/// Contract for the <c>LLAMA_WINDOWS_TELEMETRY</c> env override: an explicit
/// OFF/ON (case-insensitive) decides; unset, empty, or malformed values have
/// no opinion (null) and the default-on path applies via the setting.
/// </summary>
public class TelemetryEnvVarTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("yes")]
    [InlineData("0")]
    [InlineData("disabled")]
    [InlineData("onx")]        // anything but bare ON/OFF is malformed
    [InlineData(" true")]
    public void Unset_or_malformed_values_have_no_opinion(string? value) =>
        Assert.Null(TelemetryEnvVar.Parse(value));

    [Theory]
    [InlineData("OFF")]
    [InlineData("off")]
    [InlineData(" Off ")]
    public void Explicit_off_disables(string value) =>
        Assert.False(TelemetryEnvVar.Parse(value));

    [Theory]
    [InlineData("ON")]
    [InlineData("on")]
    [InlineData(" On ")]
    public void Explicit_on_enables(string value) =>
        Assert.True(TelemetryEnvVar.Parse(value));
}
