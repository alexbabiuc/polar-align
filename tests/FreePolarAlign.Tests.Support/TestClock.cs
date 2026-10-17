using Microsoft.Extensions.Time.Testing;

namespace FreePolarAlign.Tests.Support;

/// <summary>
/// The clock every test runs on (D37). Time is fake: it starts at a fixed
/// instant and moves only when a test moves it, so no timestamp a test sees,
/// and nothing a test asserts, comes from the real clock.
///
/// The fixed start matters beyond tidiness. It fixes the sidereal time, and
/// with it the patch of sky a sequence plans for: on the real clock the
/// convergence tests passed or failed by the hour they ran (#13).
/// </summary>
public sealed class TestClock : FakeTimeProvider
{
    /// <summary>
    /// 2026-01-15 22:00 UTC. The convergence tests were checked against the
    /// committed catalogue at this instant and at one six hours later.
    /// </summary>
    public static DateTimeOffset DefaultStart { get; } = new(2026, 1, 15, 22, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// How far one step of <see cref="RunUntilAsync{T}"/> moves time. Small
    /// against every delay the tests configure (tens of milliseconds and up), so
    /// nothing is skipped past; large enough that a twenty-second wait is two
    /// thousand steps rather than millions.
    /// </summary>
    public static TimeSpan DefaultStep { get; } = TimeSpan.FromMilliseconds(10);

    /// <summary>The default limit on a wait, in fake time.</summary>
    public static TimeSpan DefaultLimit { get; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A wait that is only blocked on real work -- a solve, a render -- gives
    /// up after this many looks. It is the one guard not in fake time, and it
    /// only ever fires on a hang: real work cannot be measured in fake time, and
    /// a guard in fake time would turn a slow machine into a failed test.
    /// </summary>
    private const int MaximumLooksWhileBusy = 600_000;

    public TestClock()
        : this(DefaultStart)
    {
    }

    public TestClock(DateTimeOffset start)
        : base(start)
    {
    }

    /// <summary>The fake time, as the engine's coordinate code takes it.</summary>
    public DateTime UtcNow => GetUtcNow().UtcDateTime;

    /// <summary>
    /// Moves time on and lets the work it releases run. The advance happens off
    /// the caller's thread, because a timer's continuation can run inline on
    /// whichever thread fires it, and that must not be the test's own.
    /// </summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        await Task.Run(() => Advance(by)).ConfigureAwait(false);
        await LetReleasedWorkRunAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Moves time forward a step at a time until <paramref name="probe"/>
    /// finds something.
    /// </summary>
    /// <param name="poll">Called between looks, as the UI's status timer would be.</param>
    /// <param name="busy">
    /// True while real work is in flight. Time does not move then, so a solve
    /// that takes real seconds is not mistaken for minutes of the sky turning.
    /// </param>
    /// <param name="limit">How much fake time may pass before giving up.</param>
    public async Task<T> RunUntilAsync<T>(
        Func<T?> probe,
        Func<string> describeFailure,
        TimeSpan? limit = null,
        Func<Task>? poll = null,
        Func<bool>? busy = null,
        TimeSpan? step = null) where T : class
    {
        DateTimeOffset deadline = GetUtcNow() + (limit ?? DefaultLimit);
        int looksWhileBusy = 0;

        while (true)
        {
            if (probe() is { } found)
            {
                return found;
            }

            if (GetUtcNow() >= deadline)
            {
                throw new TimeoutException($"Nothing after {limit ?? DefaultLimit} of fake time.{describeFailure()}");
            }

            if (poll is not null)
            {
                await poll().ConfigureAwait(false);
            }

            await LetReleasedWorkRunAsync().ConfigureAwait(false);

            if (busy?.Invoke() == true)
            {
                if (++looksWhileBusy > MaximumLooksWhileBusy)
                {
                    throw new TimeoutException($"Real work never finished.{describeFailure()}");
                }

                continue;
            }

            looksWhileBusy = 0;
            await Task.Run(() => Advance(step ?? DefaultStep)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Lets <paramref name="duration"/> of fake time pass a step at a time, so
    /// that timers fire in order and each one's consequences can schedule the
    /// next -- which one jump of the clock would not allow. What a test does to
    /// say "wait, then check nothing happened".
    /// </summary>
    public Task PassAsync(TimeSpan duration, Func<Task>? poll = null, Func<bool>? busy = null)
    {
        DateTimeOffset until = GetUtcNow() + duration;
        return RunUntilAsync(
            () => GetUtcNow() >= until ? new object() : null,
            () => string.Empty,
            duration + DefaultStep,
            poll,
            busy);
    }

    /// <summary>
    /// Moves time on until <paramref name="task"/> completes, then awaits it.
    /// For a command that waits on something only time finishes -- the engine's
    /// camera commands wait for the exposure in progress, which ends on this
    /// clock. Awaited bare, such a command waits for ever whenever an exposure
    /// happens to be running.
    /// </summary>
    public async Task CompleteAsync(Task task)
    {
        await WaitUntilAsync(() => task.IsCompleted, () => " It never completed.").ConfigureAwait(false);
        await task.ConfigureAwait(false);
    }

    /// <summary>Waits until <paramref name="condition"/> holds, moving time as <see cref="RunUntilAsync{T}"/> does.</summary>
    public Task WaitUntilAsync(
        Func<bool> condition,
        Func<string> describeFailure,
        TimeSpan? limit = null,
        Func<Task>? poll = null,
        Func<bool>? busy = null) =>
        RunUntilAsync(() => condition() ? new object() : null, describeFailure, limit, poll, busy);

    /// <summary>
    /// Gives continuations that a time step released a chance to run on the
    /// thread pool before the next look. This is scheduling, not time: nothing
    /// under test reads it, and a slower machine only makes a test slower.
    /// </summary>
    private static Task LetReleasedWorkRunAsync() => Task.Delay(1);
}
