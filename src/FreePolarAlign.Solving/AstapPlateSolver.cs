using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FreePolarAlign.Imaging.Fits;
using FreePolarAlign.Imaging.Wcs;

namespace FreePolarAlign.Solving;

/// <summary>
/// <see cref="ISolver"/> adapter over ASTAP (D3), the optional accelerator.
/// ASTAP is GPL, so it is invoked as a subprocess and never linked -- no GPL
/// obligation arises because nothing of it ships inside this process. Most
/// installs will not have ASTAP at all, which is an expected, non-exceptional
/// outcome: a missing binary maps to <see cref="PlateSolveFailureReason.SolverError"/>
/// with a readable message, not a crash.
/// </summary>
/// <remarks>
/// ASTAP's CLI is documented informally (its own <c>-h</c> output and long
/// community use in tools such as N.I.N.A. and APT) rather than by a
/// versioned API contract the way Watney's library is, so the argument names
/// and success/failure heuristics here are the best-documented, most stable
/// subset: <c>-f</c> (input file), <c>-fov</c> (diagonal field of view hint,
/// degrees), <c>-ra</c>/<c>-spd</c> (nearby-search center, RA in decimal
/// hours and south polar distance = dec + 90, both in degrees) and
/// <c>-wcs</c> (write the WCS solution into the FITS header in place). This
/// has not been exercised against a real ASTAP binary (none is installed on
/// the development machine); only the argument-building and failure-mapping
/// logic is unit-tested, plus the "binary is absent" path end to end.
/// </remarks>
public sealed class AstapPlateSolver : ISolver
{
    private readonly string _executablePath;

    /// <param name="executablePath">
    /// Path to the ASTAP command-line executable (commonly <c>astap_cli</c> on
    /// Linux/macOS or <c>astap.exe</c> on Windows). Defaults to <c>"astap"</c>,
    /// which relies on it being resolvable via PATH, since .NET's
    /// <see cref="Process"/> searches PATH for a bare filename on every
    /// supported platform.
    /// </param>
    public AstapPlateSolver(string executablePath = "astap")
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("ASTAP executable path must be provided.", nameof(executablePath));
        }

        _executablePath = executablePath;
    }

    public string Name => "ASTAP";

    public async Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
    {
        FitsImage image;
        try
        {
            image = FitsFile.Read(request.ImagePath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or FormatException)
        {
            return PlateSolveResult.Failed(PlateSolveFailureReason.InvalidImage, $"Could not read '{request.ImagePath}' as FITS: {ex.Message}");
        }

        // ASTAP's -wcs flag rewrites the input file's header in place. Solve
        // a disposable copy so a caller's original image is never mutated as
        // a side effect of plate solving.
        string workingCopyPath = Path.Combine(Path.GetTempPath(), $"fpa-astap-{Guid.NewGuid():N}.fits");
        try
        {
            File.Copy(request.ImagePath, workingCopyPath, overwrite: true);
        }
        catch (IOException ex)
        {
            return PlateSolveResult.Failed(PlateSolveFailureReason.InvalidImage, $"Could not stage '{request.ImagePath}' for ASTAP: {ex.Message}");
        }

        try
        {
            var psi = new ProcessStartInfo(_executablePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string argument in BuildArguments(request, workingCopyPath, image.Width, image.Height))
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
                    $"Could not start ASTAP at '{_executablePath}': {ex.Message}. Install ASTAP or configure its executable path.");
            }

            stopwatch.Stop();
            return MapProcessOutcome(outcome, workingCopyPath, stopwatch.Elapsed);
        }
        finally
        {
            TryDelete(workingCopyPath);
            TryDelete(workingCopyPath + ".bak");
            TryDelete(Path.ChangeExtension(workingCopyPath, ".wcs"));
            TryDelete(Path.ChangeExtension(workingCopyPath, ".ini"));
        }
    }

    /// <summary>
    /// Pure translation from the solver-agnostic request to ASTAP's CLI
    /// arguments -- unit-tested directly, without running any process.
    /// </summary>
    internal static string[] BuildArguments(PlateSolveRequest request, string imagePath, int imageWidth, int imageHeight)
    {
        var args = new List<string> { "-f", imagePath, "-wcs", "-z", "0" };

        if (request.ApproximateScaleArcsecPerPixel is { } scale)
        {
            double diagonalPixels = Math.Sqrt((double)imageWidth * imageWidth + (double)imageHeight * imageHeight);
            double fovDegrees = diagonalPixels * scale / 3600.0;
            args.Add("-fov");
            args.Add(fovDegrees.ToString("F4", CultureInfo.InvariantCulture));
        }

        bool hasPositionHint = request.ApproximateRaDegrees is not null && request.ApproximateDecDegrees is not null;
        if (hasPositionHint)
        {
            double raHours = request.ApproximateRaDegrees!.Value / 15.0;
            double southPolarDistanceDegrees = request.ApproximateDecDegrees!.Value + 90.0;
            args.Add("-ra");
            args.Add(raHours.ToString("F6", CultureInfo.InvariantCulture));
            args.Add("-spd");
            args.Add(southPolarDistanceDegrees.ToString("F6", CultureInfo.InvariantCulture));
            args.Add("-r");
            args.Add((request.SearchRadiusDegrees ?? 10.0).ToString("F3", CultureInfo.InvariantCulture));
        }

        return args.ToArray();
    }

    internal readonly record struct ProcessOutcome(int ExitCode, string StandardOutput, string StandardError);

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

    internal static PlateSolveResult MapProcessOutcome(ProcessOutcome outcome, string imagePath, TimeSpan elapsed)
    {
        string combinedOutput = outcome.StandardOutput + "\n" + outcome.StandardError;

        if (outcome.ExitCode != 0)
        {
            return ClassifyFailureText(combinedOutput);
        }

        TanWcsSolution wcs;
        int width, height;
        try
        {
            FitsImage solvedImage = FitsFile.Read(imagePath);
            wcs = TanWcsSolution.FromHeader(solvedImage.ExtraHeader);
            width = solvedImage.Width;
            height = solvedImage.Height;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or NotSupportedException or IOException or InvalidDataException)
        {
            return PlateSolveResult.Failed(PlateSolveFailureReason.SolverError,
                $"ASTAP exited successfully but no usable TAN WCS solution could be read back: {ex.Message}");
        }

        return PlateSolveResult.Succeeded(BuildSolution(wcs, width, height, combinedOutput, elapsed));
    }

    internal static PlateSolveResult ClassifyFailureText(string output)
    {
        string lower = output.ToLowerInvariant();
        if (lower.Contains("no solution") || lower.Contains("not solved") || lower.Contains("failed to solve") || lower.Contains("solution not found"))
        {
            return PlateSolveResult.Failed(PlateSolveFailureReason.NoMatchFound,
                "ASTAP could not match the field against its star database.");
        }

        if (lower.Contains("no stars") || lower.Contains("star detect") || lower.Contains("too few star"))
        {
            return PlateSolveResult.Failed(PlateSolveFailureReason.NoStarsDetected,
                "ASTAP did not detect enough stars in the image.");
        }

        return PlateSolveResult.Failed(PlateSolveFailureReason.SolverError,
            string.IsNullOrWhiteSpace(output.Trim()) ? "ASTAP exited with a non-zero status and no output." : $"ASTAP reported an error: {output.Trim()}");
    }

    /// <summary>
    /// Builds the solver-agnostic solution from ASTAP's WCS output. Pure and
    /// unit-testable against a hand-built <see cref="TanWcsSolution"/>, with
    /// no process or ASTAP install required.
    /// </summary>
    internal static PlateSolveSolution BuildSolution(TanWcsSolution wcs, int imageWidth, int imageHeight, string diagnosticOutput, TimeSpan elapsed)
    {
        // The image center, not CRVAL, is the reported center: ASTAP is not
        // guaranteed to anchor CRPIX at the image's midpoint, and re-using
        // the existing Imaging WCS math here is exact regardless of where it did.
        (double centerRa, double centerDec) = wcs.PixelToWorld(imageWidth / 2.0 + 0.5, imageHeight / 2.0 + 0.5);

        double scaleXArcsecPerPixel = 3600.0 * Math.Sqrt(wcs.Cd1_1 * wcs.Cd1_1 + wcs.Cd2_1 * wcs.Cd2_1);
        double scaleYArcsecPerPixel = 3600.0 * Math.Sqrt(wcs.Cd1_2 * wcs.Cd1_2 + wcs.Cd2_2 * wcs.Cd2_2);
        double pixelScale = 0.5 * (scaleXArcsecPerPixel + scaleYArcsecPerPixel);

        // ASTAP's CLI does not print a stable, version-independent matched-star
        // count, unlike Watney's SolveResult.StarsUsedInSolve. This is a
        // best-effort scrape of its stdout; 0 means "not reported", not
        // necessarily "zero stars matched" -- callers should treat this
        // adapter's MatchedStarCount as advisory only.
        int matchedStarCount = TryParseMatchedStarCount(diagnosticOutput);

        // Likewise, rotation is not reported directly; derive it from the CD
        // matrix rather than invent a number, the same source D12 uses for
        // parity. For the standard CD <-> CDELT/CROTA2 relation (FITS paper
        // II, Calabretta & Greisen: CD1_2 = -CDELT2*sin(CROTA2), CD2_2 =
        // CDELT2*cos(CROTA2), CDELT2 > 0 by convention), atan2(-CD1_2, CD2_2)
        // recovers CROTA2 exactly, including the unrotated case (0 degrees).
        double rotationDegrees = Math.Atan2(-wcs.Cd1_2, wcs.Cd2_2) * (180.0 / Math.PI);

        return new PlateSolveSolution(
            CenterRaDegrees: centerRa,
            CenterDecDegrees: centerDec,
            PixelScaleArcsecPerPixel: pixelScale,
            RotationDegrees: rotationDegrees,
            Cd1_1: wcs.Cd1_1,
            Cd1_2: wcs.Cd1_2,
            Cd2_1: wcs.Cd2_1,
            Cd2_2: wcs.Cd2_2,
            MatchedStarCount: matchedStarCount,
            SolveDuration: elapsed);
    }

    private static int TryParseMatchedStarCount(string output)
    {
        Match match = Regex.Match(output, @"(\d+)\s*(?:matched|matching)\s*stars?", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out int count) ? count : 0;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort temp-file cleanup; not worth failing the solve over.
        }
    }
}
