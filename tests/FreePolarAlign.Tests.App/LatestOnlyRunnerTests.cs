using FreePolarAlign.App.Services;
using Xunit;

namespace FreePolarAlign.Tests.App;

/// <summary>
/// The queue behind the live preview: one job at a time, only the newest
/// waiting. The camera runs continuously (D26) and can publish frames faster
/// than they can be read and stretched, so anything else either falls behind
/// or races.
/// </summary>
public class LatestOnlyRunnerTests
{
    private sealed record Job(int Number);

    [Fact]
    public async Task JobsArrivingWhileOneRuns_AreReplacedByTheNewest()
    {
        var release = new TaskCompletionSource();
        var ran = new List<int>();
        var runner = new LatestOnlyRunner<Job>(async job =>
        {
            lock (ran)
            {
                ran.Add(job.Number);
            }

            if (job.Number == 1)
            {
                await release.Task;
            }
        });

        runner.Submit(new Job(1));
        Assert.True(SpinWait.SpinUntil(() => { lock (ran) { return ran.Count == 1; } }, TimeSpan.FromSeconds(10)));
        for (int n = 2; n <= 5; n++)
        {
            runner.Submit(new Job(n));
        }

        release.SetResult();
        await runner.WhenIdleAsync();

        Assert.Equal(new[] { 1, 5 }, ran);
    }

    /// <summary>
    /// One job that throws must not leave the runner believing it is still
    /// busy, or the preview would freeze for the rest of the night on one bad
    /// frame.
    /// </summary>
    [Fact]
    public async Task AJobThatThrows_IsReported_AndTheNextStillRuns()
    {
        var errors = new List<Exception>();
        var ran = new List<int>();
        var runner = new LatestOnlyRunner<Job>(
            job =>
            {
                ran.Add(job.Number);
                return job.Number == 1 ? throw new InvalidDataException("bad frame") : Task.CompletedTask;
            },
            onError: errors.Add);

        runner.Submit(new Job(1));
        await runner.WhenIdleAsync();
        runner.Submit(new Job(2));
        await runner.WhenIdleAsync();

        Assert.Equal(new[] { 1, 2 }, ran);
        Assert.IsType<InvalidDataException>(Assert.Single(errors));
    }
}
