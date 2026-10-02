using System.Globalization;
using FreePolarAlign.Imaging.Fits;

namespace FreePolarAlign.Solving;

/// <summary>
/// <see cref="ISolver"/> adapter over PlaneWave's PlateSolve3 (3.80), a free
/// Windows program, run as a subprocess.
/// </summary>
/// <remarks>
/// PlaneWave publishes no document for this command line. It is taken from
/// the two open-source programs that drive it, which agree: N.I.N.A.'s adapter,
/// whose comments record PlaneWave's own description, and CCDciel's. Not run
/// against a real PlateSolve3: it is Windows-only, and the development machine
/// is a Mac.
///
/// The call is <c>PlateSolve3.80 image [ra dec width height]</c>, all four in
/// <em>radians</em>: the estimated centre and the field size. Given the image
/// alone it solves blind, as CCDciel calls it.
///
/// The result goes beside the image as <c>&lt;name&gt;_PS3.txt</c>:
/// <c>True</c> or <c>False</c>; RA and Dec (J2000, radians); the scale in
/// <em>pixels per radian</em> and a rotation in degrees from north; the match
/// method; then A, B, C, D and four more terms of the plate transformation.
/// CCDciel reads the field's parity from the signs of A to D, which this
/// follows, and the rotation as 180 degrees minus CROTA2. It also found the
/// numbers written with a decimal comma on some Windows locales, which doubles
/// the comma count on each line; both forms are read.
/// </remarks>
public sealed class PlateSolve3Solver : ExternalProcessSolver
{
    private const double ArcsecPerRadian = 206264.806;

    public PlateSolve3Solver(string executablePath)
        : base(executablePath)
    {
    }

    public override string Name => "PlateSolve3";

    private protected override IReadOnlyList<string> BuildArguments(PlateSolveRequest request, StagedFrame frame) =>
        BuildArguments(request, frame.Path, frame.Width, frame.Height, frame.Header);

    private protected override PlateSolveResult ReadResult(ProcessOutcome outcome, StagedFrame frame, TimeSpan elapsed)
    {
        string path = ResultPath(frame.Path);
        if (!File.Exists(path))
        {
            string output = (outcome.StandardOutput + "\n" + outcome.StandardError).Trim();
            return PlateSolveResult.Failed(PlateSolveFailureReason.SolverError,
                $"PlateSolve3 exited with code {outcome.ExitCode} and wrote no result file" +
                (output.Length == 0 ? "." : $": {output}") +
                " Check that its star catalogue is installed.");
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PlateSolveResult.Failed(PlateSolveFailureReason.SolverError, $"PlateSolve3's result file could not be read: {ex.Message}");
        }

        return ParseResult(lines, frame.Width, frame.Height, elapsed);
    }

    internal static string ResultPath(string imagePath) =>
        Path.Combine(Path.GetDirectoryName(imagePath) ?? string.Empty, Path.GetFileNameWithoutExtension(imagePath) + "_PS3.txt");

    /// <summary>
    /// A near solve needs both a centre and a field size. The size comes from
    /// a measured scale, or failing that from the frame's own FOCALLEN and
    /// XPIXSZ; with neither, the solve is blind even when the mount's pointing
    /// is known, since PlateSolve3 cannot be told one without the other.
    /// </summary>
    internal static string[] BuildArguments(PlateSolveRequest request, string imagePath, int imageWidth, int imageHeight, FitsHeader header)
    {
        bool hasPositionHint = request.ApproximateRaDegrees is not null && request.ApproximateDecDegrees is not null;
        double? scale = request.ApproximateScaleArcsecPerPixel is > 0.0 ? request.ApproximateScaleArcsecPerPixel : ScaleFromHeader(header);

        if (!hasPositionHint || scale is null)
        {
            return new[] { imagePath };
        }

        double toRadians = Math.PI / 180.0;
        return new[]
        {
            imagePath,
            Format(request.ApproximateRaDegrees!.Value * toRadians),
            Format(request.ApproximateDecDegrees!.Value * toRadians),
            Format(imageWidth * scale.Value / ArcsecPerRadian),
            Format(imageHeight * scale.Value / ArcsecPerRadian),
        };
    }

    private static double? ScaleFromHeader(FitsHeader header)
    {
        try
        {
            double focalLength = header.GetDouble("FOCALLEN", double.NaN);
            double pixelSize = header.GetDouble("XPIXSZ", double.NaN);
            return focalLength > 0.0 && pixelSize > 0.0 ? 206.264806 * pixelSize / focalLength : null;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException)
        {
            return null;
        }
    }

    internal static PlateSolveResult ParseResult(IReadOnlyList<string> lines, int imageWidth, int imageHeight, TimeSpan elapsed)
    {
        string status = lines.Count > 0 ? lines[0].Trim() : string.Empty;
        if (!status.Equals("True", StringComparison.OrdinalIgnoreCase))
        {
            // PlateSolve3 gives no reason for a failure, only the verdict.
            return PlateSolveResult.Failed(PlateSolveFailureReason.NoMatchFound,
                status.Length == 0 ? "PlateSolve3 wrote an empty result file." : "PlateSolve3 could not match the field.");
        }

        double[]? position = lines.Count > 1 ? Numbers(lines[1], 2) : null;
        double[]? scaleAndRotation = lines.Count > 2 ? Numbers(lines[2], 2) : null;
        if (position is null || scaleAndRotation is null || scaleAndRotation[0] <= 0.0)
        {
            return PlateSolveResult.Failed(PlateSolveFailureReason.SolverError,
                $"PlateSolve3 reported a solution but its result file was not in the expected form: {string.Join(" | ", lines)}");
        }

        double toDegrees = 180.0 / Math.PI;
        double ra = position[0] * toDegrees;
        double dec = position[1] * toDegrees;
        double scale = ArcsecPerRadian / scaleAndRotation[0];
        double crota2 = 180.0 - scaleAndRotation[1];

        (double cd11, double cd12, double cd21, double cd22) = SolutionGeometry.CdMatrix(scale, crota2, mirrored: false);

        // The transformation's own signs, negated, are the CD matrix's (CCDciel).
        // Without them the frame is taken to be unmirrored, as for ASPS.
        double[]? transform = lines.Count > 4 ? Numbers(lines[4], 8) : null;
        if (transform is not null)
        {
            cd11 = -Math.Sign(transform[0]) * Math.Abs(cd11);
            cd12 = -Math.Sign(transform[1]) * Math.Abs(cd12);
            cd21 = -Math.Sign(transform[2]) * Math.Abs(cd21);
            cd22 = -Math.Sign(transform[3]) * Math.Abs(cd22);
        }

        var wcs = new Imaging.Wcs.TanWcsSolution(
            Crpix1: imageWidth / 2.0 + 0.5,
            Crpix2: imageHeight / 2.0 + 0.5,
            Crval1Degrees: ((ra % 360.0) + 360.0) % 360.0,
            Crval2Degrees: dec,
            cd11, cd12, cd21, cd22);

        return PlateSolveResult.Succeeded(SolutionGeometry.FromWcs(wcs, imageWidth, imageHeight, matchedStarCount: 0, elapsed));
    }

    /// <summary>
    /// The first <paramref name="count"/> numbers on a line, written with a
    /// decimal point and comma separators, or -- on a decimal-comma locale --
    /// with every number split in two at its comma. Null when neither reading
    /// works.
    /// </summary>
    internal static double[]? Numbers(string line, int count)
    {
        string[] fields = line.Split(',', StringSplitOptions.TrimEntries);

        if (fields.Length >= count && fields.Length < 2 * count && TryParseAll(fields.Take(count), out double[] plain))
        {
            return plain;
        }

        if (fields.Length >= 2 * count)
        {
            IEnumerable<string> joined = Enumerable.Range(0, count).Select(i => fields[2 * i] + "." + fields[2 * i + 1]);
            if (TryParseAll(joined, out double[] comma))
            {
                return comma;
            }
        }

        return null;
    }

    private static bool TryParseAll(IEnumerable<string> fields, out double[] values)
    {
        var parsed = new List<double>();
        foreach (string field in fields)
        {
            if (!double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                values = Array.Empty<double>();
                return false;
            }

            parsed.Add(value);
        }

        values = parsed.ToArray();
        return true;
    }

    /// <summary>
    /// PlateSolve3 is distributed as a folder to unzip, so there is no fixed
    /// install location. The one tried by default is where N.I.N.A.'s
    /// documentation shows it; in a folder, the first <c>PlateSolve3*.exe</c>,
    /// the pattern N.I.N.A. browses for.
    /// </summary>
    public static string ResolveExecutable(string? configured) =>
        ResolveExecutable(configured, File.Exists, Directory.Exists, EnumerateExecutables, DefaultInstallLocations());

    internal static string ResolveExecutable(
        string? configured,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        Func<string, IEnumerable<string>> executablesIn,
        IEnumerable<string> defaultLocations) =>
        ExternalProcessSolver.ResolveExecutable(
            configured,
            defaultLocations,
            folder => executablesIn(folder)
                .Where(path => Path.GetFileName(path).StartsWith("PlateSolve3", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(),
            "PlateSolve3.80.exe",
            fileExists,
            directoryExists);

    private static IEnumerable<string> EnumerateExecutables(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.exe").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> DefaultInstallLocations()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<string>();
        }

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return new[]
        {
            Path.Combine(documents, "PlateSolve3", "PlateSolve3.80", "PlateSolve3.80.exe"),
            Path.Combine(documents, "PlateSolve3", "PlateSolve3.80.exe"),
        };
    }

    private static string Format(double value) => value.ToString("0.############", CultureInfo.InvariantCulture);
}
