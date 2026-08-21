using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Integration tests for the download-control requests
/// (<see cref="LlamaManager.CancelServerDownloadAsync"/> /
/// <see cref="LlamaManager.PauseServerDownloadAsync"/>) against a real local
/// HTTP listener. The llama.cpp router registers <c>DELETE /models</c> with
/// the model name as a QUERY parameter — no <c>/models/{name}</c> route
/// exists, so the path form 404s and the download silently keeps going
/// (the "clicking X doesn't cancel" bug); pause goes to
/// <c>POST /models/unload</c> with a JSON body, which stops the download
/// child without deleting the partial files.
/// </summary>
public class LlamaManagerDownloadControlTests
{
    private sealed record RecordedRequest(string Method, string Path, string? Model, string Body);

    [Fact]
    public async Task Cancel_And_Pause_Use_The_Routes_The_Server_Registers()
    {
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
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

        // The singleton binds the port at construction; one test class owns it.
        var mgr = LlamaManager.Initialize(port);
        const string repo = "ggml-org/gpt-oss-20b-GGUF";
        await mgr.CancelServerDownloadAsync(repo);
        await mgr.PauseServerDownloadAsync(repo);

        listener.Stop();
        await serve;

        // Cancel: DELETE /models?model=<repo> — the query-parameter form is
        // the only one the router accepts (and the one that aborts the
        // download + removes the partials server-side).
        Assert.Contains(requests, r =>
            r.Method == "DELETE" && r.Path == "/models" && r.Model == repo);

        // Pause: POST /models/unload {"model": "<repo>"} — stops the download
        // child, keeps the partial bytes for a later resume.
        var pause = Assert.Single(requests, r => r.Method == "POST" && r.Path == "/models/unload");
        using var json = JsonDocument.Parse(pause.Body);
        Assert.Equal(repo, json.RootElement.GetProperty("model").GetString());
    }

    private static int GetFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
