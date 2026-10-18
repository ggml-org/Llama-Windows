using LlamaApp.Views;
using Xunit;

namespace LlamaApp.Tests;

/// <summary>
/// Unit tests for <see cref="ModelPresence.EffectivelyPresent"/> — the gate
/// behind hiding rows the disk no longer backs. The risks of hiding are
/// pinned here: busy models always keep their rows (a mid-download has no
/// file; a loaded model may have been deleted under a running child), and
/// everything with a findable file counts as present regardless of the
/// server's listing.
/// </summary>
public class ModelPresenceTests
{
    private static HashSet<string> Present(
        string[] serverIds, string[] busy, string[] onDisk)
        => ModelPresence.EffectivelyPresent(
            serverIds,
            id => busy.Contains(id, StringComparer.OrdinalIgnoreCase),
            id => onDisk.Contains(id, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Listed_But_Fileless_Idle_Models_Are_Ghosts()
    {
        var present = Present(["ghost-Q4_0", "real-Q4_0"], [], ["real-Q4_0"]);

        Assert.Single(present);
        Assert.Contains("real-Q4_0", present);
    }

    [Theory]
    [InlineData("downloading")]   // no file yet by definition
    [InlineData("loading")]       // the child is starting
    [InlineData("loaded")]        // running — possibly deleted under it
    public void Busy_Models_Always_Keep_Their_Rows(string busyId)
    {
        var present = Present([busyId], [busyId], []);

        Assert.Contains(busyId, present);
    }

    [Fact]
    public void A_File_On_Disk_Keeps_The_Row_Even_When_Unlisted_By_Resolution_Heuristics()
        // "fileExists" is the app's own resolution — anything it finds is
        // present, full stop.
        => Assert.Contains(
            "here-Q4_0", Present(["here-Q4_0"], [], ["here-Q4_0"]));

    [Fact]
    public void Ids_The_Server_Does_Not_List_Are_Not_Present()
        => Assert.Empty(Present([], [], ["orphan-Q4_0"]));

    [Fact]
    public void Matching_Is_Case_Insensitive()
    {
        var present = Present(["Org/Repo:Q4_0"], [], ["ORG/REPO:q4_0"]);

        Assert.Single(present);
    }
}
