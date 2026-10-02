using System.Globalization;
using FreePolarAlign.Imaging.Fits;

namespace FreePolarAlign.Solving;

/// <summary>
/// <see cref="ISolver"/> adapter over All Sky Plate Solver (ASPS), a free
/// Windows program around astrometry.net, run as a subprocess.
/// </summary>
/// <remarks>
/// Written from the vendor's command-line document (astrogb.com,
/// <c>ASPS_CmdLine.pdf</c>) and checked against N.I.N.A.'s adapter, which has
/// driven it for years. Not run against a real ASPS: it is Windows-only, and
/// the development machine is a Mac.
///
/// The call is <c>PlateSolver.exe /solvefile image result focal pixel ra dec
/// radius</c>. Focal length (mm) and pixel size (microns) only matter through
/// their ratio, the scale; both zero tells ASPS to use what its own settings
/// window holds. RA and Dec are J2000 degrees; RA, Dec and radius all zero
/// asks for a blind solve, and ASPS falls back to blind itself when a near
/// solve fails.
///
/// The result is a text file: <c>OK</c> or <c>ERROR</c>, then RA and Dec
/// (degrees), field width and height (arcminutes), scale (arcsec/pixel),
/// rotation (CROTA2, degrees) and focal length, one per line. After
/// <c>ERROR</c> the remaining lines are a message.
///
/// ASPS does not report parity, so the CD matrix here assumes an unmirrored
/// frame. Nothing downstream reads the CD matrix yet; the session uses the
/// centre and the scale, both of which ASPS reports directly.
/// </remarks>
public sealed class AspsPlateSolver : ExternalProcessSolver
{
    public AspsPlateSolver(string executablePath)
        : base(executablePath)
    {
    }

    public override string Name => "ASPS";

    private protected override IReadOnlyList<string> BuildArguments(PlateSolveRequest request, StagedFrame frame) =>
        BuildArguments(request, frame.Path, ResultPath(frame), frame.Header);

    private protected override PlateSolveResult ReadResult(ProcessOutcome outcome, StagedFrame frame, TimeSpan elapsed)
    {
        string path = ResultPath(frame);
        if (!File.Exists(path))
        {
            string output = (outcome.StandardOutput + "\n" + outcome.StandardError).Trim();
            return PlateSolveResult.Failed(PlateSolveFailureReason.SolverError,
                $"ASPS exited with code {outcome.ExitCode} and wrote no result file" +
                (output.Length == 0 ? "." : $": {output}"));
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PlateSolveResult.Failed(PlateSolveFailureReason.SolverError, $"ASPS's result file could not be read: {ex.Message}");
        }

        return ParseResult(lines, frame.Width, frame.Height, elapsed);
    }

    private static string ResultPath(StagedFrame frame) => Path.ChangeExtension(frame.Path, ".txt");

    internal static string[] BuildArguments(PlateSolveRequest request, string imagePath, string resultPath, FitsHeader header)
    {
        (double focalLength, double pixelSize) = Optics(request, header);

        bool hasPositionHint = request.ApproximateRaDegrees is not null && request.ApproximateDecDegrees is not null;
        double ra = hasPositionHint ? request.ApproximateRaDegrees!.Value : 0.0;
        double dec = hasPositionHint ? request.ApproximateDecDegrees!.Value : 0.0;
        double radius = hasPositionHint ? request.SearchRadiusDegrees ?? 10.0 : 0.0;

        // Forward slashes in the image path, as N.I.N.A. passes it. The vendor
        // document does not say why, and a path ASPS cannot open fails as "File
        // not found", which is the cheaper thing to have guessed wrong.
        return new[]
        {
            "/solvefile",
            imagePath.Replace('\\', '/'),
            resultPath,
            Format(focalLength),
            Format(pixelSize),
            Format(ra),
            Format(dec),
            Format(radius),
        };
    }

    /// <summary>
    /// The focal length and pixel size to pass, which ASPS uses only as a scale.
    ///
    /// A measured scale from the request comes first. Without one, the frame's
    /// own FOCALLEN and XPIXSZ, which the capture writes whenever a focal length
    /// was entered: a claimed figure, but a far better start than whatever the
    /// ASPS settings window last held. With neither, zeros, which tell ASPS to
    /// use that window.
    /// </summary>
    private static (double FocalLengthMm, double PixelSizeMicrons) Optics(PlateSolveRequest request, FitsHeader header)
    {
        double? pixelSize = Positive(header, "XPIXSZ");

        if (request.ApproximateScaleArcsecPerPixel is { } scale && scale > 0.0)
        {
            // Any pixel size gives the right ratio; the real one keeps the focal
            // length ASPS reports back meaningful.
            double microns = pixelSize ?? 1.0;
            return (206.264806 * microns / scale, microns);
        }

        if (pixelSize is { } known && Positive(header, "FOCALLEN") is { } focalLength)
        {
            return (focalLength, known);
        }

        return (0.0, 0.0);
    }

    /// <summary>A header value that is a positive number, whether written as an integer or not.</summary>
    private static double? Positive(FitsHeader header, string keyword)
    {
        try
        {
            double value = header.GetDouble(keyword, double.NaN);
            return double.IsFinite(value) && value > 0.0 ? value : null;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException)
        {
            return null;
        }
    }

    internal static PlateSolveResult ParseResult(IReadOnlyList<string> lines, int imageWidth, int imageHeight, TimeSpan elapsed)
    {
        string status = lines.Count > 0 ? lines[0].Trim() : string.Empty;

        if (!status.Equals("OK", StringComparison.OrdinalIgnoreCase))
        {
            string message = string.Join(" ", lines.Skip(1).Select(line => line.Trim()).Where(line => line.Length > 0));
            return ClassifyError(message);
        }

        if (lines.Count < 8 ||
            !TryParse(lines[1], out double ra) ||
            !TryParse(lines[2], out double dec) ||
            !TryParse(lines[5], out double scale) ||
            !TryParse(lines[6], out double rotation) ||
            scale <= 0.0)
        {
            return PlateSolveResult.Failed(PlateSolveFailureReason.SolverError,
                $"ASPS reported OK but its result file was not in the documented form: {string.Join(" | ", lines)}");
        }

        (double cd11, double cd12, double cd21, double cd22) = SolutionGeometry.CdMatrix(scale, rotation, mirrored: false);

        return PlateSolveResult.Succeeded(new PlateSolveSolution(
            CenterRaDegrees: ra,
            CenterDecDegrees: dec,
            PixelScaleArcsecPerPixel: scale,
            RotationDegrees: rotation,
            Cd1_1: cd11,
            Cd1_2: cd12,
            Cd2_1: cd21,
            Cd2_2: cd22,
            MatchedStarCount: 0,
            SolveDuration: elapsed));
    }

    /// <summary>The error messages the vendor document lists, mapped to what they mean here.</summary>
    internal static PlateSolveResult ClassifyError(string message)
    {
        string lower = message.ToLowerInvariant();
        string text = message.Length == 0 ? "ASPS reported an error without a message." : $"ASPS: {message}";

        if (lower.Contains("cannot solve"))
        {
            return PlateSolveResult.Failed(PlateSolveFailureReason.NoMatchFound, text);
        }

        if (lower.Contains("index") || lower.Contains("astrometry.net library"))
        {
            return PlateSolveResult.Failed(PlateSolveFailureReason.SolverError,
                text + " Install the indexes for this field size from ASPS's Index Installation Wizard.");
        }

        if (lower.Contains("not found") || lower.Contains("accepts only") || lower.Contains("invalid fits") || lower.Contains("cannot copy image"))
        {
            return PlateSolveResult.Failed(PlateSolveFailureReason.InvalidImage, text);
        }

        return PlateSolveResult.Failed(PlateSolveFailureReason.SolverError, text);
    }

    /// <summary>
    /// ASPS's usual install location, an install folder, or the executable itself.
    /// </summary>
    public static string ResolveExecutable(string? configured) =>
        ResolveExecutable(configured, File.Exists, Directory.Exists, DefaultInstallLocations());

    internal static string ResolveExecutable(
        string? configured,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        IEnumerable<string> defaultLocations) =>
        ExternalProcessSolver.ResolveExecutable(
            configured,
            defaultLocations,
            folder => fileExists(Path.Combine(folder, "PlateSolver.exe")) ? Path.Combine(folder, "PlateSolver.exe") : null,
            "PlateSolver.exe",
            fileExists,
            directoryExists);

    private static IEnumerable<string> DefaultInstallLocations()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<string>();
        }

        // The location in the vendor document's own examples.
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return new[]
        {
            Path.Combine(programFilesX86, "PlateSolver", "PlateSolver.exe"),
            Path.Combine(programFiles, "PlateSolver", "PlateSolver.exe"),
        };
    }

    private static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
