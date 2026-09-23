using System.Globalization;
using System.Reflection;

namespace FreePolarAlign.App.Services;

/// <summary>
/// Which build of the application is running, for the first line of the session
/// log.
///
/// This exists because of a real half-hour wasted: a bug was fixed, the fix was
/// verified, and the next log showed the old error message -- because the run
/// came from an older published folder, not from the build containing the fix.
/// Nothing in the log said which build wrote it, so the only way to tell was to
/// recognise the wording of an error message. A log that records a failure
/// should also record what produced the failure.
///
/// The commit is the identifier that actually settles the question, so the
/// version string is the informational version, which the App project stamps
/// with the git commit at build time (see its <c>StampSourceRevision</c>
/// target). A build made outside a git checkout, or without git installed,
/// simply has no commit to report, which is why the file timestamp is reported
/// alongside it: not as precise, but always available, and enough to tell a
/// months-old published folder from this afternoon's build.
/// </summary>
public static class AppVersion
{
    /// <summary>The product's name, as it should appear to anything outside this program.</summary>
    public const string ProductName = "free-polar-align";

    /// <summary>
    /// Version and commit as one compact token -- "1.1.0+1af5065d4047" -- for
    /// recording in files rather than reading in a log. Anything captured
    /// carries this, so a frame can name the build that took it.
    /// </summary>
    public static string Identifier()
    {
        Assembly assembly = typeof(AppVersion).Assembly;

        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    /// <summary>
    /// A single line naming the running build: version, git commit when
    /// stamped, and when the assembly on disk was written.
    /// </summary>
    public static string Describe()
    {
        Assembly assembly = typeof(AppVersion).Assembly;

        string informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown version";

        // The SDK appends "+<SourceRevisionId>" to the informational version, so
        // the commit arrives already attached; splitting it out only to name it
        // makes the line readable by someone who does not know that convention.
        string[] parts = informational.Split('+', 2);
        string version = parts[0];
        string commit = parts.Length > 1 ? parts[1] : "no commit stamped";

        return $"Version {version} (commit {commit}), built {BuiltAt()}.";
    }

    /// <summary>
    /// When the running assembly was written to disk. Not the compile time --
    /// builds here are deterministic, which deliberately removes the timestamp
    /// from the binary -- but for the question being asked ("is this the build I
    /// just made, or the one I published in June?") the two are the same answer.
    /// </summary>
    private static string BuiltAt()
    {
        try
        {
            string location = typeof(AppVersion).Assembly.Location;
            if (string.IsNullOrEmpty(location))
            {
                // Single-file publish leaves no assembly path to stat.
                return "unknown";
            }

            return File.GetLastWriteTimeUtc(location).ToString("yyyy-MM-dd HH:mm:ssZ", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "unknown";
        }
    }
}
