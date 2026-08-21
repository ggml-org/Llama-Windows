using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LlamaApp.Common;
using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Integration tests for the download-cancel request
/// (<see cref="LlamaManager.CancelServerDownloadAsync"/>) and the
/// duplicate-download join (<see cref="LlamaManager.DownloadModelAsync"/>)
/// against a real local HTTP listener. Cancelling an in-flight download
/// goes to <c>POST /models/unload</c> with a JSON body: the router cancels
/// a downloading model's child process on unload. A duplicate download POST
/// is rejected with <c>"already exists"</c> — the app must join the
/// in-flight download rather than fail (the "cancel disappeared" bug).
/// </summary>
public class LlamaManagerDownloadControlTests
{
    private sealed record RecordedRequest(string Method, string Path, string? Model, string Body);

    private sealed class TestModel(string name) : IModel
    {
        public string Name { get; } = name;
        public string ServerModelId => Name;
        public string Description => "";
        public string Parameters => "";
        public string Size => "";
        public string License => "";
        public bool Vision => false;
    }

    // The manager singleton binds its port at construction and can only be
    // initialized once per process — both facts share it (tests within a
    // class never run in parallel).
    private static readonly int Port = GetFreePort();
    private static LlamaManager? _mgr;
    private static LlamaManager Mgr => _mgr ??= LlamaManager.Initialize(Port);

    [Fact]
    public async Task Cancel_Uses_The_Route_The_Server_Registers()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        listener.Start();

        var requests = new List<RecordedRequest>();
        var serve = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (HttpListenerException) { break; }  // listener stopped
                catch (ObjectDisposedException) { break; }

                var body = "";
                if (ctx.Request.HasEntityBody)
                    body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();

                // Recorded BEFORE the response goes out, so a returned client
                // call implies the request is in the list.
                lock (requests)
                    requests.Add(new RecordedRequest(
                        ctx.Request.HttpMethod,
                        ctx.Request.Url!.AbsolutePath,
                        ctx.Request.QueryString["model"],
                        body));

                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
        });

        const string repo = "ggml-org/gpt-oss-20b-GGUF";
        await Mgr.CancelServerDownloadAsync(repo);

        listener.Stop();
        await serve;

        // Cancel: POST /models/unload {"model": "<repo>"} — the router
        // cancels a downloading model's child process on unload.
        var cancel = Assert.Single(requests, r => r.Method == "POST" && r.Path == "/models/unload");
        using var json = JsonDocument.Parse(cancel.Body);
        Assert.Equal(repo, json.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Duplicate_Download_Post_Joins_The_In_Flight_Download()
    {
        const string repo = "test/join-GGUF";
        var posted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        listener.Start();

        // A mini fake router: /health + GET /models answer, POST /models
        // rejects the duplicate, and the SSE stream emits the in-flight
        // download's progress + completion once the POST has arrived.
        var serve = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                _ = Task.Run(() => Handle(ctx));
            }
        });

        async Task Handle(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url!.AbsolutePath;
            try
            {
                if (path == "/models/sse")
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.SendChunked = true;
                    // Emit the download's events once the (duplicate) POST is in.
                    await Task.WhenAny(posted.Task, Task.Delay(10000));
                    await WriteEvent(ctx, "{\"model\":\"" + repo + "\",\"event\":\"download_progress\",\"data\":{\"progress\":{\"http://x/f.gguf\":{\"done\":50,\"total\":100}}}}");
                    await WriteEvent(ctx, "{\"model\":\"" + repo + "\",\"event\":\"download_finished\",\"data\":{}}");
                    // The client breaks the loop on download_finished; closing
                    // the stream here is the server hanging up afterwards.
                    try { ctx.Response.Close(); } catch { /* already gone */ }
                    return;
                }
                if (path == "/models" && ctx.Request.HttpMethod == "POST")
                {
                    posted.TrySetResult();
                    var body = "{\"error\":{\"code\":400,\"message\":\"model '" + repo + "' already exists\",\"type\":\"invalid_request_error\"}}";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.StatusCode = 400;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                    return;
                }
                if (path == "/models" && ctx.Request.HttpMethod == "GET")
                {
                    var body = "{\"data\":[{\"id\":\"" + repo + "\",\"status\":{\"value\":\"downloading\"},\"architecture\":{\"input_modalities\":[\"text\"]},\"source\":\"cache\",\"can_remove\":true}]}";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                    return;
                }
                // /health and anything else: any response means "alive".
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
            catch (HttpListenerException) { /* client went away */ }
            catch (ObjectDisposedException) { /* listener stopped */ }
        }

        static async Task WriteEvent(HttpListenerContext ctx, string json)
        {
            var bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
            await ctx.Response.OutputStream.WriteAsync(bytes);
            await ctx.Response.OutputStream.FlushAsync();
        }

        // Adopt the fake server (flips the manager to Running).
        Assert.True(await Mgr.EnsureLlamaOrDownloadAsync());

        var reports = new List<ModelDownloadProgress>();
        var ok = await Mgr.DownloadModelAsync(
            new TestModel(repo),
            new Progress<ModelDownloadProgress>(p => reports.Add(p)));

        listener.Stop();
        await serve;

        // The duplicate POST didn't fail the download: the in-flight events
        // were joined and the completion flowed through.
        Assert.True(ok);
        Assert.Contains(reports, p => p is { DownloadedBytes: 50, TotalBytes: 100 });
        Assert.Contains(reports, p => p is { Done: true, Failed: false });
        Assert.DoesNotContain(reports, p => p.Failed);
    }

    // ----- Duplicate-download classification -------------------------------

    [Fact]
    public void Duplicate_In_Flight_Download_Is_Joined()
    {
        var body = """{"error":{"code":400,"message":"model 'x/y-GGUF' already exists","type":"invalid_request_error"}}""";
        Assert.Equal(LlamaManager.DuplicateDownloadAction.Join,
            LlamaManager.ClassifyDuplicateDownload(body, "downloading"));
    }

    [Fact]
    public void Duplicate_Already_Downloaded_Model_Completes_Immediately()
    {
        var body = """{"error":{"code":400,"message":"model 'x/y-GGUF' already exists","type":"invalid_request_error"}}""";
        Assert.Equal(LlamaManager.DuplicateDownloadAction.AlreadyComplete,
            LlamaManager.ClassifyDuplicateDownload(body, "unloaded"));
    }

    [Fact]
    public void Duplicate_With_Unknown_Status_Is_Joined()
    {
        // The status lookup failing must not turn a duplicate into a failure:
        // the "already exists" rejection proves the server knows the model.
        var body = """{"error":{"code":400,"message":"model 'x/y-GGUF' already exists","type":"invalid_request_error"}}""";
        Assert.Equal(LlamaManager.DuplicateDownloadAction.Join,
            LlamaManager.ClassifyDuplicateDownload(body, null));
    }

    [Fact]
    public void Other_Rejections_Still_Fail()
    {
        var body = """{"error":{"code":500,"message":"model validation failed, unable to download","type":"server_error"}}""";
        Assert.Equal(LlamaManager.DuplicateDownloadAction.Fail,
            LlamaManager.ClassifyDuplicateDownload(body, "downloading"));
    }

    private static int GetFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
