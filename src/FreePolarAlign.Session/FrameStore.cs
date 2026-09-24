using System.Globalization;

namespace FreePolarAlign.Session;

/// <summary>
/// Decides when a captured frame may be deleted (D26). With the camera running
/// continuously a night would otherwise leave thousands of files, but a frame
/// cannot simply be deleted when the next one arrives: the preview re-reads the
/// frame on screen when the brightness changes, and a solve may still be reading
/// an older one. So a frame lives while it is displayed or held, and is deleted
/// when it is neither.
///
/// Only paths it was told about are ever deleted. The capture directory is never
/// scanned, so a file a user saved or dropped there cannot be lost to this class.
///
/// Thread-safe: the capture loop and the solver call it from different tasks.
/// File operations happen under the lock, so a decision to delete and the delete
/// itself cannot be separated by another task taking a hold on the same frame.
/// </summary>
public sealed class FrameStore : IDisposable
{
    /// <summary>D20 was diagnosed from such frames; twenty is what D26 settled on.</summary>
    public const int DefaultFailedFramesKept = 20;

    /// <summary>
    /// Pruning matches on this prefix, so nothing else in the directory is ever
    /// deleted, even when a user keeps their own files there.
    /// </summary>
    public const string FailedFramePrefix = "solving_failed_";

    // Sortable, so ordinal order by name is chronological and pruning needs no
    // file timestamps (which a copy may or may not preserve). No colons: Windows
    // forbids them in file names.
    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'.'fff'Z'";

    private const string FailedFrameExtension = ".fits";

    // Same root as JsonFileSettingsStore.DefaultPath and SessionLog.DefaultDirectory
    // (the latter in the App project, which Session must not reference). There is
    // no shared helper; the convention is this one expression.
    public static string DefaultFailedSolvesDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".free-polar-align",
        "failed-solves");

    private readonly object _gate = new();
    private readonly string _failedSolvesDirectory;
    private readonly int _failedFramesKept;
    private readonly Action<string> _deleteFile;

    private readonly Dictionary<string, int> _holds = new(StringComparer.Ordinal);

    // Deletes that failed. On Windows the UI can still have the file open while it
    // reads a preview, and that must not throw into the capture loop; the delete is
    // simply tried again on the next call.
    private readonly HashSet<string> _pendingDeletes = new(StringComparer.Ordinal);

    private string? _displayed;
    private bool _disposed;

    public FrameStore(string failedSolvesDirectory, int failedFramesKept = DefaultFailedFramesKept)
        : this(failedSolvesDirectory, failedFramesKept, File.Delete)
    {
    }

    /// <summary>
    /// The delete is injectable because the failure it must survive -- a file
    /// another process has open -- cannot be produced on macOS or Linux, which do
    /// not enforce <see cref="FileShare.None"/>.
    /// </summary>
    internal FrameStore(string failedSolvesDirectory, int failedFramesKept, Action<string> deleteFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failedSolvesDirectory);
        ArgumentOutOfRangeException.ThrowIfNegative(failedFramesKept);
        ArgumentNullException.ThrowIfNull(deleteFile);

        _failedSolvesDirectory = Path.GetFullPath(failedSolvesDirectory);
        _failedFramesKept = failedFramesKept;
        _deleteFile = deleteFile;

        // The cap may have been lowered since the last run.
        PruneFailures();
    }

    /// <summary>
    /// A new frame has been published for display. The previously displayed frame
    /// is deleted, unless it is held.
    /// </summary>
    public void Displayed(string path)
    {
        string key = Key(path);

        lock (_gate)
        {
            ThrowIfDisposed();
            RetryPendingDeletes();

            string? previous = _displayed;
            _displayed = key;
            _pendingDeletes.Remove(key);

            if (previous is not null && previous != key)
            {
                DeleteIfUnused(previous);
            }
        }
    }

    /// <summary>
    /// Holds a frame while it is being solved. Disposing the handle releases it; a
    /// released frame is deleted unless it is still the one displayed or held by
    /// another handle. Disposing a handle twice releases it once.
    /// </summary>
    public IDisposable Hold(string path)
    {
        string key = Key(path);

        lock (_gate)
        {
            ThrowIfDisposed();
            RetryPendingDeletes();

            _holds[key] = _holds.GetValueOrDefault(key) + 1;
            _pendingDeletes.Remove(key);
        }

        return new HoldHandle(this, key);
    }

    /// <summary>
    /// A held frame's solve failed: copies it into the failed-solves directory and
    /// prunes the oldest kept failures beyond the cap. Returns the kept path, or
    /// null if it could not be kept. Never throws for an I/O failure, because a
    /// full disk must not stop a sequence; the caller logs the null.
    /// </summary>
    public string? KeepFailed(string path, DateTime utc)
    {
        string source = Key(path);

        lock (_gate)
        {
            ThrowIfDisposed();
            RetryPendingDeletes();

            if (_failedFramesKept == 0)
            {
                return null;
            }

            string? kept = null;

            try
            {
                Directory.CreateDirectory(_failedSolvesDirectory);
                kept = CopyToUniqueName(source, ToUtc(utc));
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                kept = null;
            }

            PruneFailures();
            return kept;
        }
    }

    /// <summary>
    /// Deletes every frame still tracked (camera disconnected, application
    /// closing). Kept failures stay. Handles still outstanding become inert.
    /// </summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            ReleaseAllLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            ReleaseAllLocked();
            _disposed = true;
        }
    }

    private void ReleaseAllLocked()
    {
        var tracked = new HashSet<string>(_holds.Keys, StringComparer.Ordinal);

        if (_displayed is not null)
        {
            tracked.Add(_displayed);
        }

        _displayed = null;
        _holds.Clear();

        foreach (string path in tracked)
        {
            TryDelete(path);
        }

        RetryPendingDeletes();
    }

    private void Release(string key)
    {
        lock (_gate)
        {
            // After ReleaseAll or Dispose the frame is already gone, or queued.
            if (!_holds.TryGetValue(key, out int count))
            {
                return;
            }

            if (count > 1)
            {
                _holds[key] = count - 1;
            }
            else
            {
                _holds.Remove(key);
                DeleteIfUnused(key);
            }

            RetryPendingDeletes();
        }
    }

    private void DeleteIfUnused(string key)
    {
        if (key != _displayed && !_holds.ContainsKey(key))
        {
            TryDelete(key);
        }
    }

    private void RetryPendingDeletes()
    {
        if (_pendingDeletes.Count == 0)
        {
            return;
        }

        foreach (string path in _pendingDeletes.ToArray())
        {
            TryDelete(path);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            _deleteFile(path);
            _pendingDeletes.Remove(path);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone with its directory; nothing left to delete.
            _pendingDeletes.Remove(path);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            _pendingDeletes.Add(path);
        }
    }

    private string CopyToUniqueName(string source, DateTime utc)
    {
        string stem = FailedFramePrefix + utc.ToString(TimestampFormat, CultureInfo.InvariantCulture);

        // Two failures in the same millisecond get _2, _3... '_' sorts after '.',
        // so a suffixed name still sorts after the one it collided with.
        for (int attempt = 1; ; attempt++)
        {
            string name = attempt == 1 ? stem : $"{stem}_{attempt}";
            string destination = Path.Combine(_failedSolvesDirectory, name + FailedFrameExtension);

            if (File.Exists(destination))
            {
                continue;
            }

            File.Copy(source, destination, overwrite: false);
            return destination;
        }
    }

    private void PruneFailures()
    {
        string[] failures;

        try
        {
            if (!Directory.Exists(_failedSolvesDirectory))
            {
                return;
            }

            failures = Directory.GetFiles(_failedSolvesDirectory, FailedFramePrefix + "*" + FailedFrameExtension);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return;
        }

        // GetFiles' pattern matching is looser than it looks on Windows (8.3 names,
        // and "*.fits" also matching "*.fitsx"), so check the name exactly.
        string[] ours = failures
            .Where(f =>
            {
                string name = Path.GetFileName(f);
                return name.StartsWith(FailedFramePrefix, StringComparison.Ordinal)
                    && name.EndsWith(FailedFrameExtension, StringComparison.Ordinal);
            })
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToArray();

        for (int i = 0; i < ours.Length - _failedFramesKept; i++)
        {
            try
            {
                _deleteFile(ours[i]);
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                // Pruned again on the next failure or the next start.
            }
        }
    }

    private static DateTime ToUtc(DateTime time) => time.Kind switch
    {
        DateTimeKind.Local => time.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(time, DateTimeKind.Utc),
        _ => time,
    };

    private static string Key(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private static bool IsFileSystemFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class HoldHandle(FrameStore store, string key) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                store.Release(key);
            }
        }
    }
}
