using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Unit tests for parsing the <c>llama fit-params --fit-print on</c> output
/// into <see cref="FitParamsEstimate"/>s — real-world captures (stdout
/// estimate rows, stderr log noise around them, ANSI color codes on the
/// anchor line) plus the malformed shapes that must yield no verdict.
/// </summary>
public class FitParamsQueryTests
{
    [Fact]
    public void Parse_Single_Device_And_Host_From_A_Real_Capture()
    {
        // Real capture: the anchor is a stderr log line, the rows are stdout
        // with trailing spaces, and the two streams land combined.
        var output = """
            0.00.016.585 I llama_fit_params: printing estimated memory in MiB to stdout (device, model, context, compute) ...
            CUDA0 680 38 514 
            Host 306 0 9 
            """;

        var estimate = FitParamsQuery.Parse(output);

        Assert.NotNull(estimate);
        var device = Assert.Single(estimate.Devices);
        Assert.Equal("CUDA0", device.Name);
        Assert.Equal(680UL << 20, device.ModelBytes);
        Assert.Equal(38UL << 20, device.ContextBytes);
        Assert.Equal(514UL << 20, device.ComputeBytes);
        Assert.Equal("Host", estimate.Host.Name);
        Assert.Equal(306UL << 20, estimate.Host.ModelBytes);
        Assert.Equal(0UL, estimate.Host.ContextBytes);
        Assert.Equal((680UL + 38 + 514 + 306 + 0 + 9) << 20, estimate.TotalBytes);
    }

    [Fact]
    public void Parse_Ignores_Backend_Noise_And_Ansi_Codes_Before_The_Anchor()
    {
        var output =
            "load_backend: loaded CUDA backend from /app/libggml-cuda.so\n" +
            "\u001b[34m0.00.015.973\u001b[0m \u001b[32mI \u001b[0mllama_fit_params: printing estimated memory in MiB to stdout (device, model, context, compute) ...\n" +
            "Vulkan0 4096 128 700\n" +
            "Host 102 0 12\n";

        var estimate = FitParamsQuery.Parse(output);

        Assert.NotNull(estimate);
        var device = Assert.Single(estimate.Devices);
        Assert.Equal("Vulkan0", device.Name);
        Assert.Equal(4096UL << 20, device.ModelBytes);
        Assert.Equal(102UL << 20, estimate.Host.ModelBytes);
    }

    [Fact]
    public void Parse_Multiple_Devices_Sum_Into_TotalBytes()
    {
        var output = """
            llama_fit_params: printing estimated memory in MiB to stdout (device, model, context, compute) ...
            CUDA0 8000 500 600 
            Vulkan0 6000 300 400 
            Host 200 0 50 
            """;

        var estimate = FitParamsQuery.Parse(output);

        Assert.NotNull(estimate);
        Assert.Equal(2, estimate.Devices.Count);
        Assert.Equal((8000UL + 500 + 600 + 6000 + 300 + 400 + 200 + 0 + 50) << 20,
            estimate.TotalBytes);
    }

    [Fact]
    public void Parse_Host_Only_On_A_Cpu_Only_Probe()
    {
        var output = """
            llama_fit_params: printing estimated memory in MiB to stdout (device, model, context, compute) ...
            Host 4200 800 900 
            """;

        var estimate = FitParamsQuery.Parse(output);

        Assert.NotNull(estimate);
        Assert.Empty(estimate.Devices);
        Assert.Equal((4200UL + 800 + 900) << 20, estimate.TotalBytes);
    }

    [Fact]
    public void Parse_Without_The_Host_Row_Is_No_Verdict()
    {
        // A truncated section is an unknown verdict, not a zero one.
        var output = """
            llama_fit_params: printing estimated memory in MiB to stdout (device, model, context, compute) ...
            CUDA0 680 38 514 
            """;

        Assert.Null(FitParamsQuery.Parse(output));
    }

    [Fact]
    public void Parse_Without_The_Anchor_Is_No_Verdict()
    {
        // The launcher's "unknown command" output contains no section.
        var output = "error: unknown command 'fit-params'\nusage: llama [options] <command>\n";

        Assert.Null(FitParamsQuery.Parse(output));
    }

    [Fact]
    public void Parse_Skips_Malformed_Rows_Inside_The_Section()
    {
        var output = """
            llama_fit_params: printing estimated memory in MiB to stdout (device, model, context, compute) ...
            CUDA0 not-a-number 38 514
            CUDA1 680 38 514 
            Host 306 0 9 
            """;

        var estimate = FitParamsQuery.Parse(output);

        Assert.NotNull(estimate);
        var device = Assert.Single(estimate.Devices);
        Assert.Equal("CUDA1", device.Name);
    }
}
