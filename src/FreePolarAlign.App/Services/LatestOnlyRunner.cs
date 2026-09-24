namespace FreePolarAlign.App.Services;

/// <summary>
/// Runs one job at a time, and keeps only the newest of those waiting.
///
/// Built for the live preview. With the camera running continuously (D26)
/// frames arrive every 0.1 to 2 s, and reading and stretching one can take
/// longer than that on a large sensor. Loading every frame in turn would fall
/// further behind with each one; loading them concurrently would race, and the
/// slowest would win. Only the newest frame is worth showing, so a frame that
/// arrives while one is loading replaces whatever was waiting, and the frames
/// in between are never loaded at all.
/// </summary>
/// <remarks>
/// Thread-safe: jobs are submitted from the UI thread and run on the pool.
/// </remarks>
internal sealed class LatestOnlyRunner<T>
    where T : class
{
    private readonly Func<T, Task> _run;
    private readonly Func<T, T, T> _merge;
    private readonly Action<Exception>? _onError;
    private readonly object _gate = new();

    private bool _running;
    private T? _waiting;
    private Task _loop = Task.CompletedTask;

    /// <param name="merge">
    /// Which of (the job already waiting, the one arriving) to keep. Defaults to
    /// the arriving one. The preview overrides it so that a re-stretch cannot
    /// displace a newer frame, which is rendered at the current stretch anyway.
    /// </param>
    /// <param name="onError">
    /// A job that throws is reported and the runner carries on. Without that, one
    /// bad frame would leave the runner believing it was still busy and the
    /// preview frozen for the rest of the night.
    /// </param>
    public LatestOnlyRunner(Func<T, Task> run, Func<T, T, T>? merge = null, Action<Exception>? onError = null)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _merge = merge ?? ((_, arriving) => arriving);
        _onError = onError;
    }

    public void Submit(T job)
    {
        ArgumentNullException.ThrowIfNull(job);

        lock (_gate)
        {
            if (_running)
            {
                _waiting = _waiting is null ? job : _merge(_waiting, job);
                return;
            }

            _running = true;
            _loop = Task.Run(() => RunAsync(job));
        }
    }

    /// <summary>Completes once nothing is running or waiting. For tests.</summary>
    internal Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return _loop;
        }
    }

    private async Task RunAsync(T job)
    {
        while (true)
        {
            try
            {
                await _run(job).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
            }

            lock (_gate)
            {
                if (_waiting is null)
                {
                    _running = false;
                    return;
                }

                job = _waiting;
                _waiting = null;
            }
        }
    }
}
