using System.Globalization;
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
public sealed class AstapPlateSolver : ExternalProcessSolver
{
    /// <param name="executablePath">
    /// Path to the ASTAP executable, either the GUI build or <c>astap_cli</c>:
    /// both take the same arguments. <see cref="ResolveExecutable(string?)"/>
    /// turns what a user is likely to type into one. Defaults to
    /// <c>"astap"</c>, which relies on PATH.
    /// </param>
    public AstapPlateSolver(string executablePath = "astap")
        : base(executablePath)
    {
    }

    public override string Name => "ASTAP";

    private protected override IReadOnlyList<string> BuildArguments(PlateSolveRequest request, StagedFrame frame) =>
        BuildArguments(request, frame.Path, frame.Width, frame.Height);

    private protected override PlateSolveResult ReadResult(ProcessOutcome outcome, StagedFrame frame, TimeSpan elapsed) =>
        MapProcessOutcome(outcome, Path.ChangeExtension(frame.Path, ".ini"), frame.Width, frame.Height, elapsed);

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
        // ASTAP's CLI does not print a stable, version-independent matched-star
        // count, unlike Watney's SolveResult.StarsUsedInSolve. This is a
        // best-effort scrape of its stdout; 0 means "not reported", not
        // necessarily "zero stars matched" -- callers should treat this
        // adapter's MatchedStarCount as advisory only.
        return SolutionGeometry.FromWcs(wcs, imageWidth, imageHeight, TryParseMatchedStarCount(diagnosticOutput), elapsed);
    }

    private static int TryParseMatchedStarCount(string output)
    {
        Match match = Regex.Match(output, @"(\d+)\s*(?:matched|matching)\s*stars?", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out int count) ? count : 0;
    }

    /// <summary>
    /// The ASTAP to run for what was typed in the settings window: nothing (the
    /// usual install location), a macOS app bundle, an install folder, or the
    /// executable itself.
    /// </summary>
    public static string ResolveExecutable(string? configured) =>
        ResolveExecutable(configured, File.Exists, Directory.Exists, DefaultInstallLocations());

    internal static string ResolveExecutable(
        string? configured,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        IEnumerable<string> defaultLocations)
    {
        return ExternalProcessSolver.ResolveExecutable(
            configured, defaultLocations, InFolder, "astap", fileExists, directoryExists);

        string? InFolder(string folder)
        {
            if (folder.TrimEnd('/').EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(folder, "Contents", "MacOS", "astap");
            }

            // The command-line build first where both are installed: it is the
            // one that cannot stop to show a dialog.
            string[] names = { "astap_cli", "astap_cli.exe", "astap", "astap.exe" };
            return names.Select(name => Path.Combine(folder, name)).FirstOrDefault(fileExists);
        }
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
}
