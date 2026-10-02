using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using FreePolarAlign.Imaging.Fits;

namespace FreePolarAlign.Solving;

/// <summary>
/// What every solver run as a separate program shares: a disposable copy of the
/// frame to solve, the process itself, the timeout and cancellation, and the
/// readable failure when the program is not there.
///
/// The copy is made because these programs write beside their input -- ASTAP a
/// <c>.ini</c> and a <c>.wcs</c>, PlateSolve3 a <c>_PS3.txt</c> -- and some
/// rewrite the input itself, and the caller's frame must come back exactly as
/// it went in. Each solve gets a folder of its own, deleted afterwards, so
/// whatever a solver leaves behind goes with it without this class having to
/// know every file name.
/// </summary>
public abstract class ExternalProcessSolver : ISolver
{
    private protected ExternalProcessSolver(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("An executable path must be provided.", nameof(executablePath));
        }

        ExecutablePath = executablePath;
    }

    public abstract string Name { get; }

    private protected string ExecutablePath { get; }

    /// <summary>The staged copy of the frame, with what was read from it.</summary>
    internal sealed record StagedFrame(string Path, int Width, int Height, FitsHeader Header);

    internal readonly record struct ProcessOutcome(int ExitCode, string StandardOutput, string StandardError);

    private protected abstract IReadOnlyList<string> BuildArguments(PlateSolveRequest request, StagedFrame frame);

    private protected abstract PlateSolveResult ReadResult(ProcessOutcome outcome, StagedFrame frame, TimeSpan elapsed);

    public async Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        FitsImage image;
        try
        {
            image = FitsFile.Read(request.ImagePath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or FormatException)
        {
            return PlateSolveResult.Failed(PlateSolveFailureReason.InvalidImage, $"Could not read '{request.ImagePath}' as FITS: {ex.Message}");
        }

        string folder = Path.Combine(Path.GetTempPath(), "FreePolarAlign", "solve", Guid.NewGuid().ToString("N"));
        string stagedPath = Path.Combine(folder, "frame.fits");
        try
        {
            Directory.CreateDirectory(folder);
            File.Copy(request.ImagePath, stagedPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteFolder(folder);
            return PlateSolveResult.Failed(PlateSolveFailureReason.InvalidImage, $"Could not stage '{request.ImagePath}' for {Name}: {ex.Message}");
        }

        try
        {
            var frame = new StagedFrame(stagedPath, image.Width, image.Height, image.ExtraHeader);

            var psi = new ProcessStartInfo(ExecutablePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = folder,
            };
            foreach (string argument in BuildArguments(request, frame))
            {
                psi.ArgumentList.Add(argument);
            }

            using var timeoutCts = new CancellationTokenSource();
            if (request.Timeout is { } timeout)
            {
                timeoutCts.CancelAfter(timeout);
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var stopwatch = Stopwatch.StartNew();
            ProcessOutcome outcome;
            try
            {
                outcome = await RunAsync(psi, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                PlateSolveFailureReason reason = CancellationClassification.Classify(cancellationToken, timeoutCts);
                return PlateSolveResult.Failed(reason, CancellationClassification.Message(reason, Name));
            }
            catch (Win32Exception ex)
            {
                return PlateSolveResult.Failed(PlateSolveFailureReason.SolverError,
                    $"Could not start {Name} at '{ExecutablePath}': {ex.Message}. Install {Name} or set its path in Settings.");
            }

            stopwatch.Stop();
            return ReadResult(outcome, frame, stopwatch.Elapsed);
        }
        finally
        {
            TryDeleteFolder(folder);
        }
    }

    private static async Task<ProcessOutcome> RunAsync(ProcessStartInfo psi, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort cleanup; the process may have already exited.
            }

            throw;
        }

        return new ProcessOutcome(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// Turns what a user is likely to type into the settings window -- nothing,
    /// an install folder, or the executable itself -- into an executable to run.
    ///
    /// Left empty, the first default location that exists is used, so a stock
    /// install works without anything typed. The check is only that a file is
    /// there: whether it runs is learned from the first solve, which reports
    /// a program that will not start as a solver error naming the path.
    /// </summary>
    /// <param name="inFolder">Finds the executable inside a folder the user gave, or null.</param>
    /// <param name="bareName">What to run when nothing is configured or installed, relying on PATH.</param>
    internal static string ResolveExecutable(
        string? configured,
        IEnumerable<string> defaultLocations,
        Func<string, string?> inFolder,
        string bareName,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return defaultLocations.FirstOrDefault(fileExists) ?? bareName;
        }

        string path = configured.Trim();
        if (!directoryExists(path))
        {
            return path;
        }

        return inFolder(path) ?? Path.Combine(path, bareName);
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary folder is untidy, not harmful, and this runs
            // in a finally block where throwing would replace a solve result.
        }
    }
}
