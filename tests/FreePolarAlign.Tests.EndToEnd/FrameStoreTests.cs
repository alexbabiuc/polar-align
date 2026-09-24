using FreePolarAlign.Session;
using Xunit;

namespace FreePolarAlign.Tests.EndToEnd;

/// <summary>
/// With the camera running continuously (D26) frames are deleted once used. Two
/// failures matter: deleting too early loses the frame the preview re-reads or a
/// solve is still reading, and deleting too late fills the disk over a night.
/// Real files in a throwaway directory, never the user's profile.
/// </summary>
public sealed class FrameStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _failedDirectory;

    public FrameStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "fpa-frame-store-tests-" + Guid.NewGuid().ToString("N"));
        _failedDirectory = Path.Combine(_directory, "failed-solves");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private string Frame(string name)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, name);
        return path;
    }

    private string[] KeptFailures() =>
        Directory.GetFiles(_failedDirectory, FrameStore.FailedFramePrefix + "*")
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray()!;

    /// <summary>The preview re-reads the frame on screen when the brightness changes.</summary>
    [Fact]
    public void The_displayed_frame_survives_until_the_next_one_replaces_it()
    {
        using var store = new FrameStore(_failedDirectory);
        string first = Frame("f1.fits");
        string second = Frame("f2.fits");

        store.Displayed(first);
        Assert.True(File.Exists(first));

        store.Displayed(second);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void A_held_frame_survives_being_replaced_on_screen_and_is_deleted_on_release()
    {
        using var store = new FrameStore(_failedDirectory);
        string first = Frame("f1.fits");

        store.Displayed(first);
        IDisposable hold = store.Hold(first);
        store.Displayed(Frame("f2.fits"));

        Assert.True(File.Exists(first));

        hold.Dispose();
        Assert.False(File.Exists(first));
    }

    [Fact]
    public void A_frame_displayed_and_held_is_deleted_only_when_both_are_gone()
    {
        using var store = new FrameStore(_failedDirectory);
        string frame = Frame("f1.fits");

        store.Displayed(frame);
        store.Hold(frame).Dispose();
        Assert.True(File.Exists(frame));

        IDisposable first = store.Hold(frame);
        IDisposable second = store.Hold(frame);
        store.Displayed(Frame("f2.fits"));
        first.Dispose();
        first.Dispose();
        Assert.True(File.Exists(frame));

        second.Dispose();
        Assert.False(File.Exists(frame));
    }

    [Fact]
    public void A_kept_failure_is_named_by_its_timestamp_and_the_original_is_still_deleted_on_release()
    {
        using var store = new FrameStore(_failedDirectory);
        string frame = Frame("f1.fits");
        var utc = new DateTime(2026, 9, 24, 1, 2, 3, 4, DateTimeKind.Utc);

        IDisposable hold = store.Hold(frame);
        string? kept = store.KeepFailed(frame, utc);
        string? sameMillisecond = store.KeepFailed(frame, utc);
        hold.Dispose();

        Assert.Equal(Path.Combine(Path.GetFullPath(_failedDirectory), "solving_failed_20260924T010203.004Z.fits"), kept);
        Assert.NotNull(sameMillisecond);
        Assert.NotEqual(kept, sameMillisecond);
        Assert.Equal(new[] { Path.GetFileName(kept)!, Path.GetFileName(sameMillisecond)! }, KeptFailures());
        Assert.Equal("f1.fits", File.ReadAllText(kept!));
        Assert.False(File.Exists(frame));
    }

    /// <summary>
    /// Only the newest twenty are kept, and pruning must never touch a file it
    /// did not write: users keep their own frames beside these for comparison.
    /// </summary>
    [Fact]
    public void Kept_failures_are_capped_to_the_newest_and_nothing_else_in_the_directory_is_touched()
    {
        Directory.CreateDirectory(_failedDirectory);
        string usersOwn = Path.Combine(_failedDirectory, "my_reference.fits");
        File.WriteAllText(usersOwn, "mine");

        using var store = new FrameStore(_failedDirectory);
        var start = new DateTime(2026, 9, 24, 1, 0, 0, DateTimeKind.Utc);
        var expected = new List<string>();

        for (int i = 0; i < 25; i++)
        {
            string frame = Frame($"f{i}.fits");
            using (store.Hold(frame))
            {
                expected.Add(Path.GetFileName(store.KeepFailed(frame, start.AddSeconds(i)))!);
            }
        }

        Assert.Equal(expected.Skip(5), KeptFailures());
        Assert.True(File.Exists(usersOwn));
    }

    [Fact]
    public void A_lowered_cap_prunes_on_startup()
    {
        var start = new DateTime(2026, 9, 24, 1, 0, 0, DateTimeKind.Utc);
        using (var generous = new FrameStore(_failedDirectory, failedFramesKept: 10))
        {
            for (int i = 0; i < 10; i++)
            {
                generous.KeepFailed(Frame($"f{i}.fits"), start.AddSeconds(i));
            }
        }

        using var strict = new FrameStore(_failedDirectory, failedFramesKept: 3);

        Assert.Equal(3, KeptFailures().Length);
        Assert.EndsWith("010009.000Z.fits", KeptFailures()[^1]);
    }

    [Fact]
    public void A_failure_that_cannot_be_kept_returns_null_rather_than_throwing()
    {
        using var store = new FrameStore(_failedDirectory);

        Assert.Null(store.KeepFailed(Path.Combine(_directory, "never-written.fits"), DateTime.UtcNow));
    }

    /// <summary>
    /// On Windows the UI can hold the file open while it reads a preview. That is
    /// simulated through the delete seam, because macOS and Linux do not enforce
    /// FileShare.None and so cannot produce the failure.
    /// </summary>
    [Fact]
    public void A_delete_that_fails_is_retried_on_a_later_call_and_does_not_throw()
    {
        var locked = new HashSet<string>();
        using var store = new FrameStore(_failedDirectory, FrameStore.DefaultFailedFramesKept, path =>
        {
            if (locked.Contains(path))
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }

            File.Delete(path);
        });

        string first = Frame("f1.fits");
        string second = Frame("f2.fits");
        locked.Add(first);

        store.Displayed(first);
        store.Displayed(second);
        Assert.True(File.Exists(first));

        locked.Clear();
        store.Displayed(Frame("f3.fits"));

        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
    }

    [Fact]
    public void Release_all_deletes_every_tracked_frame_including_those_still_locked_once_free()
    {
        bool isLocked = true;
        string stuck = Frame("stuck.fits");
        var store = new FrameStore(_failedDirectory, FrameStore.DefaultFailedFramesKept, path =>
        {
            if (isLocked && path == stuck)
            {
                throw new IOException("locked");
            }

            File.Delete(path);
        });

        string held = Frame("held.fits");
        string shown = Frame("shown.fits");
        string untracked = Frame("saved-by-user.fits");
        store.Displayed(stuck);
        store.Displayed(shown);
        IDisposable hold = store.Hold(held);
        string? kept = store.KeepFailed(held, DateTime.UtcNow);

        store.ReleaseAll();
        Assert.False(File.Exists(held));
        Assert.False(File.Exists(shown));
        Assert.True(File.Exists(stuck));

        isLocked = false;
        store.Dispose();
        hold.Dispose();

        Assert.False(File.Exists(stuck));
        Assert.True(File.Exists(untracked));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void A_frame_already_gone_is_not_an_error()
    {
        using var store = new FrameStore(_failedDirectory);
        string frame = Frame("f1.fits");

        store.Displayed(frame);
        File.Delete(frame);
        store.Displayed(Frame("f2.fits"));
        store.ReleaseAll();
    }

    /// <summary>The capture loop and the solver call from different tasks.</summary>
    [Fact]
    public async Task Concurrent_callers_neither_throw_nor_leak_frames()
    {
        using var store = new FrameStore(_failedDirectory);
        const int frames = 400;
        string[] paths = Enumerable.Range(0, frames).Select(i => Frame($"f{i}.fits")).ToArray();
        int keptCount = 0;
        int displayed = 0;

        Task capture = Task.Run(() =>
        {
            foreach (string path in paths)
            {
                store.Displayed(path);
                Interlocked.Increment(ref displayed);
            }
        });

        // A frame is only ever held once it has been displayed, which is the
        // engine's order: both happen as the frame arrives, under its gate.
        // Holding one before it is displayed and releasing it again would delete
        // it before it could be shown -- correctly, and not what this tests.
        Task[] solvers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = worker; i < frames; i += 8)
            {
                SpinWait.SpinUntil(() => Volatile.Read(ref displayed) > i);
                using (store.Hold(paths[i]))
                {
                    if (i % 50 == 0)
                    {
                        // A frame the capture loop already superseded is gone, so
                        // not every one of these can be kept.
                        if (store.KeepFailed(paths[i], DateTime.UtcNow) is not null)
                        {
                            Interlocked.Increment(ref keptCount);
                        }
                    }
                }
            }
        })).ToArray();

        await Task.WhenAll(solvers.Append(capture));

        Assert.Equal(new[] { paths[^1] }, paths.Where(File.Exists));

        store.ReleaseAll();
        Assert.DoesNotContain(paths, File.Exists);
        Assert.Equal(keptCount, KeptFailures().Length);
    }
}
