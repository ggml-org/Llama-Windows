using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="MissingSnapshotTracker"/> — the hysteresis behind
/// removing rows whose model vanished from the server's list (file deleted
/// outside the app). A false "expired" flickers rows away on a transient miss;
/// a false "kept" is the stale-row bug this replaces.
/// </summary>
public class MissingSnapshotTrackerTests
{
    [Fact]
    public void One_Miss_Never_Removes_A_Row()
    {
        var tracker = new MissingSnapshotTracker(threshold: 3);

        Assert.Empty(tracker.Observe(["a", "b"], ["a"]));
        Assert.Empty(tracker.Observe(["a", "b"], ["a"]));
    }

    [Fact]
    public void A_Row_Expires_After_Consecutive_Misses()
    {
        var tracker = new MissingSnapshotTracker(threshold: 3);

        Assert.Empty(tracker.Observe(["a", "b"], ["a"]));   // miss 1
        Assert.Empty(tracker.Observe(["a", "b"], ["a"]));   // miss 2
        Assert.Equal(new[] { "b" }, tracker.Observe(["a", "b"], ["a"])); // miss 3 → expire
    }

    [Fact]
    public void Reappearing_Rows_Reset_Their_Miss_Count()
    {
        var tracker = new MissingSnapshotTracker(threshold: 2);

        Assert.Empty(tracker.Observe(["a"], []));      // miss 1
        Assert.Empty(tracker.Observe(["a"], ["a"]));   // back → reset
        Assert.Empty(tracker.Observe(["a"], []));      // miss 1 again
        Assert.Equal(new[] { "a" }, tracker.Observe(["a"], [])); // miss 2 → expire
    }

    [Fact]
    public void Seen_Rows_Are_Never_Expired()
    {
        var tracker = new MissingSnapshotTracker(threshold: 1);

        Assert.Empty(tracker.Observe(["a", "b"], ["a", "b", "c"]));
    }

    [Fact]
    public void Expired_Rows_Are_Forgotten_Not_Retriggered()
    {
        var tracker = new MissingSnapshotTracker(threshold: 1);

        Assert.Equal(new[] { "a" }, tracker.Observe(["a"], []));
        // Next observation with the same gap starts a fresh count (the row is
        // gone by then — the caller removed it), not an immediate re-expiry.
        Assert.Empty(tracker.Observe([], []));
    }

    [Fact]
    public void Ids_Are_Matched_Case_Insensitively()
    {
        var tracker = new MissingSnapshotTracker(threshold: 3);

        Assert.Empty(tracker.Observe(["Org/Repo:Q4_0"], ["ORG/REPO:q4_0"]));
    }

    [Fact]
    public void Rows_No_Longer_Listed_Are_Forgotten()
    {
        // A row removed by some other path must not keep accruing misses for
        // an id that no longer exists.
        var tracker = new MissingSnapshotTracker(threshold: 2);

        Assert.Empty(tracker.Observe(["gone"], []));  // miss 1
        Assert.Empty(tracker.Observe([], []));        // row gone from the list entirely
        Assert.Empty(tracker.Observe(["gone"], []));  // fresh count, not "miss 2"
    }
}
