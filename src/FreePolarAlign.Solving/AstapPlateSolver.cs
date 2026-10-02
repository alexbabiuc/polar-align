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
/// versioned API contract the way Watney's library is, so only the
/// best-documented, most stable subset is used: <c>-f</c> (input file),
/// <c>-fov</c> (field <em>height</em> in degrees, 0 for automatic),
/// <c>-ra</c>/<c>-spd</c>/<c>-r</c> (search centre as RA in decimal hours and
/// south polar distance = dec + 90, and search radius) and <c>-wcs</c>.
///
/// Checked against ASTAP v2026.09.15 on macOS arm64 with the D05 database, on
/// simulator frames of known pointing. Three things the documentation does not
/// make obvious, all of which an earlier version of this adapter got wrong:
/// <c>-fov</c> is the height and not the diagonal (ASTAP's own "inexact
/// scale" warning names the height); the answer is written to a <c>.ini</c>
/// beside the input and never into the FITS header; and the exit code says why
/// a solve failed, which the text does only loosely.
///
/// With no usable star database the GUI build shows a dialog and waits rather
/// than exiting, so that case surfaces as a timeout, not a readable error.
/// </remarks>
public sealed class AstapPlateSolver : ISolver
{
    private readonly string _executablePath;

    /// <param name="executablePath">
    /// Path to the ASTAP executable, either the GUI build or <c>astap_cli</c>:
    /// both take the same arguments. <see cref="ResolveExecutable"/> turns what
    /// a user is likely to type into one. Defaults to <c>"astap"</c>, which
    /// relies on PATH, since .NET's <see cref="Process"/> searches PATH for a
    /// bare filename on every supported platform.
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
            return MapProcessOutcome(outcome, Path.ChangeExtension(workingCopyPath, ".ini"), image.Width, image.Height, stopwatch.Elapsed);
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
    ///
    /// Every search parameter is passed even when there is no hint for it,
    /// because ASTAP otherwise falls back on whatever its own settings file
    /// last held -- a field size or search radius from someone else's session
    /// in the GUI.
    /// </summary>
    internal static string[] BuildArguments(PlateSolveRequest request, string imagePath, int imageWidth, int imageHeight)
    {
        var args = new List<string> { "-f", imagePath, "-wcs", "-z", "0" };

        double fovDegrees = request.ApproximateScaleArcsecPerPixel is { } scale
            ? imageHeight * scale / 3600.0
            : 0.0;
        args.Add("-fov");
        args.Add(fovDegrees.ToString("F4", CultureInfo.InvariantCulture));

        bool hasPositionHint = request.ApproximateRaDegrees is not null && request.ApproximateDecDegrees is not null;
        if (!hasPositionHint)
        {
            args.Add("-r");
            args.Add("180");
        }
        else
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

    /// <summary>
    /// Reads ASTAP's verdict from the <c>.ini</c> it writes beside the input,
    /// falling back on the exit code when there is none. The <c>.ini</c> is
    /// written on failure too, with an <c>ERROR=</c> line, which is the most
    /// specific explanation ASTAP gives.
    /// </summary>
    internal static PlateSolveResult MapProcessOutcome(ProcessOutcome outcome, string iniPath, int imageWidth, int imageHeight, TimeSpan elapsed)
    {
        string combinedOutput = outcome.StandardOutput + "\n" + outcome.StandardError;

        IReadOnlyDictionary<string, string> ini;
        try
        {
            ini = File.Exists(iniPath) ? ReadIni(iniPath) : new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PlateSolveResult.Failed(PlateSolveFailureReason.SolverError,
                $"ASTAP's result file '{iniPath}' could not be read: {ex.Message}");
        }

        if (outcome.ExitCode == 0 && ini.TryGetValue("PLTSOLVD", out string? solved) && solved == "T")
        {
            TanWcsSolution? wcs = WcsFromIni(ini);
            if (wcs is null)
            {
                return PlateSolveResult.Failed(PlateSolveFailureReason.SolverError,
                    "ASTAP reported a solution but its result file did not carry a complete CD-matrix WCS.");
            }

            return PlateSolveResult.Succeeded(BuildSolution(wcs, imageWidth, imageHeight, combinedOutput, elapsed));
        }

        string detail = ini.TryGetValue("ERROR", out string? error) && !string.IsNullOrWhiteSpace(error)
            ? error.Trim()
            : combinedOutput.Trim();

        if (outcome.ExitCode == 0)
        {
            return PlateSolveResult.Failed(PlateSolveFailureReason.SolverError,
                "ASTAP exited successfully but wrote no solution" +
                (string.IsNullOrEmpty(detail) ? "." : $": {detail}"));
        }

        return ClassifyExitCode(outcome.ExitCode, detail);
    }

    /// <summary>
    /// ASTAP's documented exit codes. Text matching is kept for any other code,
    /// since a newer ASTAP may add some and its wording is the only clue then.
    /// </summary>
    internal static PlateSolveResult ClassifyExitCode(int exitCode, string detail)
    {
        string suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" ({detail})";

        return exitCode switch
        {
            1 => PlateSolveResult.Failed(PlateSolveFailureReason.NoMatchFound,
                "ASTAP could not match the field against its star database" + suffix + "."),
            2 => PlateSolveResult.Failed(PlateSolveFailureReason.NoStarsDetected,
                "ASTAP did not detect enough stars in the image" + suffix + "."),
            16 => PlateSolveResult.Failed(PlateSolveFailureReason.InvalidImage,
                "ASTAP could not read the image" + suffix + "."),
            32 or 33 => PlateSolveResult.Failed(PlateSolveFailureReason.SolverError,
                "ASTAP could not find or read its star database" + suffix +
                ". Install one that covers this field size (D05 for 0.6-6 degree fields, G05 for wider)."),
            _ => ClassifyFailureText(detail),
        };
    }

    private static Dictionary<string, string> ReadIni(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadAllLines(path))
        {
            int equals = line.IndexOf('=');
            if (equals > 0)
            {
                values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
            }
        }

        return values;
    }

    private static TanWcsSolution? WcsFromIni(IReadOnlyDictionary<string, string> ini)
    {
        string[] keys = { "CRPIX1", "CRPIX2", "CRVAL1", "CRVAL2", "CD1_1", "CD1_2", "CD2_1", "CD2_2" };
        var values = new double[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            if (!ini.TryGetValue(keys[i], out string? text) ||
                !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                return null;
            }
        }

        return new TanWcsSolution(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7]);
    }

    internal static PlateSolveResult ClassifyFailureText(string output)
    {
        string lower = output.ToLowerInvariant();
        if (lower.Contains("no solution") || lower.Contains("not solved") || lower.Contains("failed to solve") || lower.Contains("solution not found"))
        {
            return PlateSolveResult.Failed(PlateSolveFailureReason.NoMatchFound,
                "ASTAP could not match the field against its star database.");
        }

        if (lower.Contains("no stars") || lower.Contains("star detect") || lower.Contains("too few star") || lower.Contains("not enough stars"))
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

    /// <summary>
    /// Turns what a user is likely to type into the settings window -- nothing,
    /// a macOS app bundle, an install folder, or the executable itself -- into
    /// an executable to run.
    ///
    /// Left empty, the platform's usual install location is tried, so a stock
    /// ASTAP install works without anything typed. The check is only that a
    /// file is there: whether it runs is learned from the first solve, which
    /// falls back to the internal solver when it does not.
    /// </summary>
    public static string ResolveExecutable(string? configured) =>
        ResolveExecutable(configured, File.Exists, Directory.Exists, DefaultInstallLocations());

    internal static string ResolveExecutable(
        string? configured,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        IEnumerable<string> defaultLocations)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return defaultLocations.FirstOrDefault(fileExists) ?? "astap";
        }

        string path = configured.Trim();
        if (!directoryExists(path))
        {
            return path;
        }

        if (path.TrimEnd('/').EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(path, "Contents", "MacOS", "astap");
        }

        // The command-line build first where both are installed: it is the
        // one that cannot stop to show a dialog.
        string[] names = { "astap_cli", "astap_cli.exe", "astap", "astap.exe" };
        return names.Select(name => Path.Combine(path, name)).FirstOrDefault(fileExists)
            ?? Path.Combine(path, OperatingSystem.IsWindows() ? "astap.exe" : "astap");
    }

    private static IEnumerable<string> DefaultInstallLocations()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new[] { "/Applications/ASTAP.app/Contents/MacOS/astap", "/usr/local/bin/astap_cli" };
        }

        if (OperatingSystem.IsWindows())
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return new[] { Path.Combine(programFiles, "astap", "astap_cli.exe"), Path.Combine(programFiles, "astap", "astap.exe") };
        }

        return new[] { "/opt/astap/astap_cli", "/opt/astap/astap", "/usr/local/bin/astap_cli", "/usr/local/bin/astap" };
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
