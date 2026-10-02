using System.Collections.Concurrent;
using System.Text;
using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="AvatarFetchGate"/> — the per-author in-flight
/// coalescing + global concurrency bound behind <see cref="AvatarCache"/>'s
/// fetch-on-miss path. The gate is storage- and XAML-free, so it runs in the
/// unpackaged test host with a stub fetch delegate (no HTTP, no live Hub).
/// </summary>
public class AvatarFetchGateTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static byte[] BytesFor(string author) => Encoding.UTF8.GetBytes("avatar:" + author);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ----- Coalescing -------------------------------------------------------

    [Fact]
    public async Task ConcurrentCalls_ForSameAuthor_FetchExactlyOnce()
    {
        var entered = NewSignal();
        var release = NewSignal();
        var calls = 0;

        var gate = new AvatarFetchGate(4, async author =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task;
            return (byte[]?)BytesFor(author);
        });

        var tasks = Enumerable.Range(0, 8).Select(_ => gate.FetchAsync("OrgX")).ToArray();

        await entered.Task.WaitAsync(Timeout);
        Assert.Equal(1, Volatile.Read(ref calls));

        release.TrySetResult();
        var results = await Task.WhenAll(tasks).WaitAsync(Timeout);

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.All(results, r => Assert.Equal(BytesFor("OrgX"), r));
    }

    [Fact]
    public async Task ConcurrentCalls_CaseInsensitiveAuthor_FetchExactlyOnce()
    {
        var entered = NewSignal();
        var release = NewSignal();
        var calls = 0;

        var gate = new AvatarFetchGate(4, async author =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task;
            return (byte[]?)BytesFor("TheOrg");
        });

        var tasks = new[] { gate.FetchAsync("TheOrg"), gate.FetchAsync("theorg"), gate.FetchAsync("THEORG") };

        await entered.Task.WaitAsync(Timeout);
        Assert.Equal(1, Volatile.Read(ref calls));

        release.TrySetResult();
        var results = await Task.WhenAll(tasks).WaitAsync(Timeout);

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.All(results, r => Assert.Equal(BytesFor("TheOrg"), r));
    }

    // ----- Distinct authors -------------------------------------------------

    [Fact]
    public async Task DistinctAuthors_FetchIndependently()
    {
        var counts = new ConcurrentDictionary<string, int>();

        var gate = new AvatarFetchGate(4, author =>
        {
            counts.AddOrUpdate(author, 1, (_, n) => n + 1);
            return Task.FromResult<byte[]?>(BytesFor(author));
        });

        var a = await gate.FetchAsync("Alpha").WaitAsync(Timeout);
        var b = await gate.FetchAsync("Beta").WaitAsync(Timeout);
        var c = await gate.FetchAsync("Gamma").WaitAsync(Timeout);

        Assert.Equal(BytesFor("Alpha"), a);
        Assert.Equal(BytesFor("Beta"), b);
        Assert.Equal(BytesFor("Gamma"), c);
        Assert.Equal(1, counts["Alpha"]);
        Assert.Equal(1, counts["Beta"]);
        Assert.Equal(1, counts["Gamma"]);
    }

    // ----- Concurrency bound ------------------------------------------------

    [Fact]
    public async Task ConcurrentFetches_RespectConcurrencyLimit()
    {
        const int limit = 2;
        var current = 0;
        var observedMax = 0;

        var entered = new ConcurrentDictionary<string, TaskCompletionSource>();
        var release = new ConcurrentDictionary<string, TaskCompletionSource>();

        var gate = new AvatarFetchGate(limit, async author =>
        {
            var now = Interlocked.Increment(ref current);
            InterlockedMax(ref observedMax, now);
            entered.GetOrAdd(author, _ => NewSignal()).TrySetResult();
            await release.GetOrAdd(author, _ => NewSignal()).Task;
            Interlocked.Decrement(ref current);
            return (byte[]?)BytesFor(author);
        });

        var tasks = new[] { gate.FetchAsync("a"), gate.FetchAsync("b"), gate.FetchAsync("c") };

        // Only <limit> fetches may start; the third must wait for a slot.
        await WaitUntil(() => entered.Count >= limit, "two fetches entered").WaitAsync(Timeout);
        Assert.Equal(limit, entered.Count);
        Assert.False(entered.ContainsKey("c"));

        // Freeing a slot lets the third author start — the limit is exercised.
        var first = entered.Keys.First();
        release.GetOrAdd(first, _ => NewSignal()).TrySetResult();
        await WaitUntil(() => entered.Count >= 3, "third fetch entered").WaitAsync(Timeout);

        foreach (var key in release.Keys.ToArray())
            release.GetOrAdd(key, _ => NewSignal()).TrySetResult();
        await Task.WhenAll(tasks).WaitAsync(Timeout);

        Assert.Equal(limit, Volatile.Read(ref observedMax));
        Assert.True(Volatile.Read(ref observedMax) > 1, "limit must actually be exercised");
    }

    // ----- Cache-filename sanitization --------------------------------------

    [Theory]
    [InlineData("ggml-org", "ggml-org")]
    [InlineData("Org.X", "org_x")]
    [InlineData("a/b", "a_b")]
    [InlineData("..", "__")]
    [InlineData("", "_")]
    [InlineData("héllo", "h_llo")]
    [InlineData("tab\there", "tab_here")]
    public void SanitizeFileName_Reduces_Author_Names_To_Safe_Stems(string author, string expected)
    {
        // Author names come from Hub data and shape a file path segment —
        // separators and dots must never survive.
        Assert.Equal(expected, AvatarCache.SanitizeFileName(author));
    }

    // ----- Failure isolation + retryability ---------------------------------

    [Fact]
    public async Task Failure_DoesNotAffectOtherAuthors_AndIsRetryable()
    {
        var badCalls = 0;

        var gate = new AvatarFetchGate(4, async author =>
        {
            if (author == "bad" && Interlocked.Increment(ref badCalls) == 1)
                throw new InvalidOperationException("boom");
            return (byte[]?)BytesFor(author);
        });

        var badTask = gate.FetchAsync("bad");
        var good = await gate.FetchAsync("good").WaitAsync(Timeout);

        Assert.Equal(BytesFor("good"), good);
        await Assert.ThrowsAsync<InvalidOperationException>(() => badTask.WaitAsync(Timeout));
        Assert.Equal(1, badCalls);

        // The failed entry must not linger: a retry runs a fresh fetch.
        var retried = await gate.FetchAsync("bad").WaitAsync(Timeout);
        Assert.Equal(BytesFor("bad"), retried);
        Assert.Equal(2, badCalls);
    }

    [Fact]
    public async Task NullResult_IsNotCached_AndIsRetryable()
    {
        var calls = 0;

        var gate = new AvatarFetchGate(4, _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<byte[]?>(null);
        });

        Assert.Null(await gate.FetchAsync("noavatar").WaitAsync(Timeout));
        Assert.Null(await gate.FetchAsync("noavatar").WaitAsync(Timeout));

        // A null (no-avatar) result leaves no residue — the next attempt retries.
        Assert.Equal(2, calls);
    }

    // ----- Blank author -----------------------------------------------------

    [Fact]
    public async Task BlankAuthor_ReturnsNull_WithoutFetching()
    {
        var calls = 0;

        var gate = new AvatarFetchGate(4, _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<byte[]?>(BytesFor("x"));
        });

        Assert.Null(await gate.FetchAsync("").WaitAsync(Timeout));
        Assert.Null(await gate.FetchAsync("   ").WaitAsync(Timeout));
        Assert.Equal(0, calls);
    }

    // ----- AvatarCache disk-only / fail-soft contract -----------------------

    [Fact]
    public async Task GetAsync_IsDiskOnly_AndFailsSoft()
    {
        // GetAsync's body contains no network call: with storage unavailable
        // (PathFor → null) or an empty cache it simply returns null.
        Assert.Null(await AvatarCache.GetAsync("some-org").WaitAsync(Timeout));
    }

    [Fact]
    public async Task GetOrFetchAsync_WithoutStorage_ShortCircuits()
    {
        // In the unpackaged test host PathFor() yields null, so GetOrFetchAsync
        // returns before reaching the fetch gate — no HTTP, no live Hub.
        Assert.Null(await AvatarCache.GetOrFetchAsync("some-org").WaitAsync(Timeout));
    }

    // ----- Helpers ----------------------------------------------------------

    private static async Task WaitUntil(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Timed out waiting for: " + message);
            await Task.Delay(15);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
            Interlocked.CompareExchange(ref target, value, current);
    }
}
