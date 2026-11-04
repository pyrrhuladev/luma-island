using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Luma.Island.Services;

internal static class Program
{
    private sealed record Cover(string Name);
    private static int _checks;
    private static readonly TimeSpan[] FastRetries = { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero };

    [STAThread]
    private static int Main()
    {
        try
        {
            RunAsync().WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            using var disabled = new MediaService();
            disabled.SetEnabledAsync(false).GetAwaiter().GetResult();
            disabled.InitializeAsync().GetAwaiter().GetResult();
            Check(!disabled.IsEnabled && disabled.Current == MediaSnapshot.Empty,
                "Disabled MediaService initialization cannot query SMTC or retain an old cover");
            disabled.Dispose();
            Check(disabled.Current == MediaSnapshot.Empty, "Disposed MediaService clears its public snapshot");
            Console.WriteLine($"PASS: {_checks} media artwork regression checks.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static async Task RunAsync()
    {
        await NewTitlePrecedesCoverAsync();
        await SameMetadataRefreshesCoverAsync();
        await RetryGetsFreshMetadataAsync();
        await NewTitleWithOldThumbnailRecoversAsync();
        await SharedAlbumCoverIsBoundedAsync();
        await SharedCandidateIsVisibleBeforeRetryAsync();
        await SameKeyEventPreservesPendingConfirmationAsync();
        await TransientErrorsRecoverAsync();
        await StaleArtworkCannotWinAsync();
        await TrackCycleCannotRestoreStaleCoverAsync();
        await StormHasOneActiveAndLatestPendingAsync();
        await BurstIsCoalescedBeforeIoAsync();
        await NullRetriesAreBoundedAsync();
        await ResetAndDisableCancelRetriesAsync();
        await ResetRejectsOldSessionCompletionAsync();
        await DisposalCancelsAndRejectsFurtherWorkAsync();
        await AcceptedTrackCommandWaitsForNewMetadataAsync();
    }

    private static MediaArtworkCoordinator<Cover> Fast(Action<MediaArtworkState<Cover>>? publish = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Action<Exception>? report = null)
        => new(publish ?? (_ => { }), report, FastRetries, delay ?? ((_, _) => Task.CompletedTask), TimeSpan.Zero);
    private static MediaArtworkInput<Cover> Input(string title, Cover? cover)
        => new(title, "artist", "album", _ => Task.FromResult(cover));
    private static Task<MediaArtworkInput<Cover>> Metadata(string title, Cover? cover) => Task.FromResult(Input(title, cover));

    private static async Task NewTitlePrecedesCoverAsync()
    {
        var a = new Cover("a"); var b = new Cover("b");
        var snapshots = new List<MediaArtworkState<Cover>>();
        using var coordinator = Fast(snapshots.Add);
        await coordinator.RefreshAsync(_ => Metadata("A", a));
        var image = new TaskCompletionSource<Cover?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task refresh = coordinator.RefreshAsync(_ => Task.FromResult(new MediaArtworkInput<Cover>("B", "artist", "album", _ => image.Task)));
        Check(coordinator.Current is { Title: "B", Artwork: null }, "A new track clears the old artwork before the new image is ready");
        Check(!refresh.IsCompleted, "Publishing the new title does not wait for thumbnail I/O");
        image.SetResult(b); await refresh;
        Check(coordinator.Current?.Artwork == b, "The delayed new cover is eventually published");
        Check(snapshots.Any(state => state.Title == "B" && state.Artwork is null), "Consumers observe the title change with a neutral cover first");
    }

    private static async Task SameMetadataRefreshesCoverAsync()
    {
        using var coordinator = Fast();
        var old = new Cover("old"); var newer = new Cover("new bytes");
        await coordinator.RefreshAsync(_ => Metadata("same title", old));
        await coordinator.RefreshAsync(_ => Metadata("same title", newer));
        Check(coordinator.Current?.Artwork == newer, "A real metadata event replaces an existing cover even when title/artist/album match");
        int reads = 0;
        var delayed = new Cover("new late bytes");
        await coordinator.RefreshAsync(_ => Metadata("same title", ++reads == 1 ? null : delayed));
        Check(reads == 2 && coordinator.Current?.Artwork == delayed,
            "A temporary same-key null thumbnail cannot permanently retain the previous non-null cover");
    }

    private static async Task RetryGetsFreshMetadataAsync()
    {
        using var coordinator = Fast();
        int reads = 0;
        var late = new Cover("late thumbnail reference");
        await coordinator.RefreshAsync(_ => Metadata("A", ++reads < 3 ? null : late));
        Check(reads == 3 && coordinator.Current?.Artwork == late, "A missing thumbnail is retried with freshly queried metadata, not the old reference");
        await coordinator.RefreshAsync(_ => Metadata("A", new Cover("late same-key event")));
        Check(coordinator.Current?.Artwork?.Name == "late same-key event", "A later same-key metadata event remains effective after earlier retries succeeded");
    }

    private static async Task TransientErrorsRecoverAsync()
    {
        var errors = new List<Exception>();
        using var coordinator = Fast(report: errors.Add);
        int reads = 0;
        var cover = new Cover("recovered");
        await coordinator.RefreshAsync(_ =>
        {
            reads++;
            if (reads == 1) return Task.FromException<MediaArtworkInput<Cover>>(new IOException("transient metadata"));
            if (reads == 2) return Task.FromResult(new MediaArtworkInput<Cover>("A", "artist", "album", _ => Task.FromException<Cover?>(new IOException("transient image"))));
            return Metadata("A", cover);
        });
        Check(reads == 3 && coordinator.Current?.Artwork == cover, "Temporary metadata and thumbnail failures recover within the same bounded request");
        Check(errors.Count == 2, "Transient faults are observed without escaping as unhandled tasks");
    }

    private static async Task NewTitleWithOldThumbnailRecoversAsync()
    {
        var snapshots = new List<MediaArtworkState<Cover>>();
        using var coordinator = Fast(snapshots.Add);
        var old = new Cover("old bytes"); var newer = new Cover("new bytes");
        await coordinator.RefreshAsync(_ => Metadata("A", old));
        int reads = 0;
        await coordinator.RefreshAsync(_ => Metadata("B", ++reads == 1 ? old : newer));
        Check(reads == 2 && coordinator.Current?.Title == "B" && coordinator.Current.Artwork == newer,
            "A new title with a stale non-null thumbnail gets fresh bytes without another event");
        Check(snapshots.Any(state => state.Title == "B" && state.Artwork == old),
            "A non-null candidate is visible while the delayed replacement is checked");
    }

    private static async Task SharedAlbumCoverIsBoundedAsync()
    {
        using var coordinator = Fast();
        var shared = new Cover("shared album bytes");
        await coordinator.RefreshAsync(_ => Metadata("A", shared));
        int reads = 0;
        await coordinator.RefreshAsync(_ => { reads++; return Metadata("B", shared); });
        Check(reads == 4 && coordinator.Current?.Title == "B" && coordinator.Current.Artwork == shared,
            "An unchanged album image is accepted after the bounded four-attempt confirmation");
        await Task.Delay(20);
        Check(reads == 4, "A shared album cover never starts permanent polling");
    }

    private static async Task SharedCandidateIsVisibleBeforeRetryAsync()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = Fast(delay: (_, token) => { waiting.TrySetResult(); return Task.Delay(Timeout.Infinite, token); });
        var shared = new Cover("shared");
        await coordinator.RefreshAsync(_ => Metadata("A", shared));
        Task refresh = coordinator.RefreshAsync(_ => Metadata("B", shared));
        await waiting.Task;
        Check(coordinator.Current?.Title == "B" && coordinator.Current.Artwork == shared && !refresh.IsCompleted,
            "A valid shared album image is shown immediately rather than after all retries");
        coordinator.Reset();
        await refresh.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static async Task SameKeyEventPreservesPendingConfirmationAsync()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int delays = 0;
        using var coordinator = Fast(delay: (_, token) =>
        {
            if (++delays == 1) { waiting.TrySetResult(); return Task.Delay(Timeout.Infinite, token); }
            return Task.CompletedTask;
        });
        var old = new Cover("A"); var newer = new Cover("B");
        await coordinator.RefreshAsync(_ => Metadata("A", old));
        Task initial = coordinator.RefreshAsync(_ => Metadata("B", old));
        await waiting.Task;
        int reads = 0;
        Task newerEvent = coordinator.RefreshAsync(_ => Metadata("B", ++reads == 1 ? old : newer));
        await Task.WhenAll(initial, newerEvent);
        Check(reads == 2 && coordinator.Current?.Artwork == newer,
            "Another same-key event cannot cancel the pending old-cover confirmation permanently");
    }

    private static async Task StaleArtworkCannotWinAsync()
    {
        var snapshots = new List<MediaArtworkState<Cover>>();
        using var coordinator = Fast(snapshots.Add);
        var stale = new Cover("stale"); var current = new Cover("current");
        var image = new TaskCompletionSource<Cover?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task first = coordinator.RefreshAsync(_ => Task.FromResult(new MediaArtworkInput<Cover>("A", "artist", "album", _ => image.Task)));
        Task second = coordinator.RefreshAsync(_ => Metadata("B", current));
        image.SetResult(stale);
        await Task.WhenAll(first, second);
        Check(coordinator.Current is { Title: "B" } && coordinator.Current.Artwork == current, "A stale image completion cannot overwrite a newer track");
        Check(!snapshots.Any(state => state.Artwork == stale), "A canceled request never publishes its late image");
    }

    private static async Task TrackCycleCannotRestoreStaleCoverAsync()
    {
        using var coordinator = Fast();
        var oldA = new TaskCompletionSource<Cover?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task first = coordinator.RefreshAsync(_ => Task.FromResult(new MediaArtworkInput<Cover>("A", "artist", "album", _ => oldA.Task)));
        int bReads = 0;
        Task middle = coordinator.RefreshAsync(_ => { bReads++; return Metadata("B", new Cover("B")); });
        var newA = new Cover("new A");
        Task last = coordinator.RefreshAsync(_ => Metadata("A", newA));
        oldA.SetResult(new Cover("old A"));
        await Task.WhenAll(first, middle, last);
        Check(coordinator.Current?.Title == "A" && coordinator.Current.Artwork == newA, "A→B→A uses request generations rather than matching titles to accept an image");
        Check(bReads == 0, "An already superseded pending track is never queried");
    }

    private static async Task StormHasOneActiveAndLatestPendingAsync()
    {
        using var coordinator = Fast();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0, active = 0, maximum = 0;
        async Task<MediaArtworkInput<Cover>> ReadAsync(int index)
        {
            reads++; active++; maximum = Math.Max(maximum, active);
            try { if (index == 0) await release.Task; return Input("track " + index, new Cover("cover " + index)); }
            finally { active--; }
        }
        var requests = new List<Task> { coordinator.RefreshAsync(_ => ReadAsync(0)) };
        for (int index = 1; index <= 30; index++) { int capture = index; requests.Add(coordinator.RefreshAsync(_ => ReadAsync(capture))); }
        Check(reads == 1 && active == 1, "An event storm never starts parallel metadata I/O");
        release.SetResult();
        await Task.WhenAll(requests);
        Check(reads == 2 && maximum == 1 && coordinator.Current?.Title == "track 30", "Thirty events retain only one active request and the latest pending request");
    }

    private static async Task BurstIsCoalescedBeforeIoAsync()
    {
        using var coordinator = new MediaArtworkCoordinator<Cover>(_ => { });
        int reads = 0;
        var requests = new List<Task>();
        for (int index = 0; index < 30; index++)
            requests.Add(coordinator.RefreshAsync(_ => { reads++; return Metadata("A", new Cover("A")); }));
        await Task.WhenAll(requests);
        Check(reads == 1, "The short coalescing window combines a synchronous burst before querying SMTC");
    }

    private static async Task NullRetriesAreBoundedAsync()
    {
        int reads = 0, waits = 0;
        using var coordinator = Fast(delay: (_, _) => { waits++; return Task.CompletedTask; });
        await coordinator.RefreshAsync(_ => { reads++; return Metadata("without cover", null); });
        Check(reads == 4 && waits == 3, "A permanently missing thumbnail has only four attempts and three delays");
        await Task.Delay(20);
        Check(reads == 4 && coordinator.Current?.Artwork is null, "Exhausted retries do not turn into permanent polling");
    }

    private static async Task ResetAndDisableCancelRetriesAsync()
    {
        int reads = 0;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = Fast(delay: (_, token) => { waiting.TrySetResult(); return Task.Delay(Timeout.Infinite, token); });
        Task refresh = coordinator.RefreshAsync(_ => { reads++; return Metadata("A", null); });
        await waiting.Task;
        coordinator.Reset();
        await refresh.WaitAsync(TimeSpan.FromSeconds(1));
        Check(reads == 1 && coordinator.Current is null, "Disable/session reset cancels a delayed retry immediately");
        var cover = new Cover("new session");
        await coordinator.RefreshAsync(_ => Metadata("A", cover));
        Check(coordinator.Current?.Artwork == cover, "A re-enabled session can load the same metadata again");
    }

    private static async Task ResetRejectsOldSessionCompletionAsync()
    {
        using var coordinator = Fast();
        var old = new TaskCompletionSource<MediaArtworkInput<Cover>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task first = coordinator.RefreshAsync(_ => old.Task);
        coordinator.Reset();
        var fresh = new Cover("enabled again");
        Task next = coordinator.RefreshAsync(_ => Metadata("same session reference", fresh));
        old.SetResult(Input("same session reference", new Cover("disabled generation")));
        await Task.WhenAll(first, next);
        Check(coordinator.Current?.Artwork == fresh, "Off→on rejects an old completion even when the new session and metadata look identical");
    }

    private static async Task DisposalCancelsAndRejectsFurtherWorkAsync()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        var coordinator = Fast(delay: (_, token) => { waiting.TrySetResult(); return Task.Delay(Timeout.Infinite, token); });
        Task current = coordinator.RefreshAsync(_ => { reads++; return Metadata("A", null); });
        await waiting.Task;
        coordinator.Dispose();
        await current.WaitAsync(TimeSpan.FromSeconds(1));
        await coordinator.RefreshAsync(_ => { reads++; return Metadata("B", new Cover("B")); });
        Check(reads == 1 && coordinator.Current is null, "Dispose cancels retries and cannot start new work");
    }

    private static async Task AcceptedTrackCommandWaitsForNewMetadataAsync()
    {
        using var coordinator = Fast();
        var old = new Cover("A"); var newer = new Cover("B");
        await coordinator.RefreshAsync(_ => Metadata("A", old));
        int reads = 0;
        await coordinator.RefreshAsync(_ => ++reads == 1 ? Metadata("A", old) : Metadata("B", newer), expectTrackChange: true);
        Check(reads == 2 && coordinator.Current?.Artwork == newer, "Accepted next/previous commands wait past the old metadata/cover");
        reads = 0;
        await coordinator.RefreshAsync(_ => { reads++; return Metadata("B", newer); }, expectTrackChange: true);
        Check(reads == 4, "An accepted command with unchanged metadata still stops after bounded retries");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        _checks++;
    }
}
