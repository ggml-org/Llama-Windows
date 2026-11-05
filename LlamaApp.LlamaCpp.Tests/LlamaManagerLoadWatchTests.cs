using System.Net;
using System.Net.Http;
using System.Text;
using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

/// <summary>
/// Regression tests for the attempt-aware /models/sse load state reducer and
/// the bounded header-opening fallback used before a load POST is sent.
/// </summary>
public sealed class LlamaManagerLoadWatchTests
{
    [Fact]
    public void Unloaded_Before_This_Attempt_Loads_Is_Not_A_Rollback()
    {
        // Opening the stream before POST is required to catch fast progress
        // events, but an old state notification can precede this attempt.
        var state = new LlamaManager.LoadWatchState();

        Assert.Equal(LlamaManager.LoadWatchAction.Ignore, state.Observe("unloaded"));
        Assert.Equal(LlamaManager.LoadWatchAction.Loading, state.Observe("loading"));
        Assert.Equal(LlamaManager.LoadWatchAction.Loaded, state.Observe("loaded"));
    }

    [Fact]
    public void Unloaded_After_Loading_Is_A_Real_Load_Failure()
    {
        var state = new LlamaManager.LoadWatchState();

        Assert.Equal(LlamaManager.LoadWatchAction.Loading, state.Observe("loading"));
        Assert.Equal(LlamaManager.LoadWatchAction.Failed, state.Observe("unloaded"));
    }

    [Fact]
    public void Subsequent_Unloaded_Remains_Failed_Without_A_New_Watcher()
    {
        var state = new LlamaManager.LoadWatchState();

        state.Observe("loading");
        Assert.Equal(LlamaManager.LoadWatchAction.Failed, state.Observe("unloaded"));
        Assert.Equal(LlamaManager.LoadWatchAction.Failed, state.Observe("unloaded"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sleeping")]
    [InlineData("downloading")]
    public void Unrelated_Statuses_Do_Not_Change_The_Load_Attempt(string status)
    {
        var state = new LlamaManager.LoadWatchState();

        Assert.Equal(LlamaManager.LoadWatchAction.Ignore, state.Observe(status));
        Assert.Equal(LlamaManager.LoadWatchAction.Ignore, state.Observe("unloaded"));
    }

    [Fact]
    public async Task Stalled_Sse_Headers_Time_Out_And_Select_Poller_Fallback()
    {
        var handler = new StallUntilCanceledHandler();
        using var client = Client(handler);

        var opening = LlamaManager.OpenSseWithHeaderTimeoutAsync(
            client, TimeSpan.FromMilliseconds(25), CancellationToken.None);
        var finished = await Task.WhenAny(opening, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(opening, finished);
        Assert.Null(await opening);
        Assert.True(handler.CancellationObserved);
    }

    [Fact]
    public async Task Sse_Headers_Arriving_In_Time_Keep_The_Response_Stream()
    {
        using var client = Client(new DelegateHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new DelayedContent(
                    "event: ready\n\n", TimeSpan.FromMilliseconds(75)),
            })));

        using var response = await LlamaManager.OpenSseWithHeaderTimeoutAsync(
            client, TimeSpan.FromMilliseconds(25), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("event: ready\n\n", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Caller_Cancellation_Is_Not_Swallowed_As_A_Header_Timeout()
    {
        using var client = Client(new StallUntilCanceledHandler());
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LlamaManager.OpenSseWithHeaderTimeoutAsync(
                client, TimeSpan.FromSeconds(5), cancel.Token));
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("http://127.0.0.1:9931"),
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private sealed class StallUntilCanceledHandler : HttpMessageHandler
    {
        public bool CancellationObserved { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }

    private sealed class DelayedContent(string text, TimeSpan delay) : HttpContent
    {
        private readonly byte[] _payload = Encoding.UTF8.GetBytes(text);

        protected override async Task SerializeToStreamAsync(
            Stream stream, TransportContext? context)
        {
            // The body arrives after the header budget. Disposing the header
            // timeout must not cancel or otherwise invalidate this read.
            await Task.Delay(delay);
            await stream.WriteAsync(_payload);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _payload.Length;
            return true;
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
