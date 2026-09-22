using System.Globalization;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Session;

namespace FreePolarAlign.App.Services;

/// <summary>
/// Writes the session to a file, one line per event.
///
/// The on-screen log is bounded and disappears when the window closes, which is
/// the wrong shape for the question this file has to answer: "what happened last
/// night, and was the number it gave me trustworthy?" So every event is written
/// through, with a UTC timestamp, in the order it arrived, alongside the inputs
/// that cannot be reconstructed afterwards -- the site, the focal length, the
/// devices, and every coordinate the mount was sent to.
///
/// Each line is flushed as it is written. The most likely way an observing
/// session ends is not an orderly shutdown, and a buffered log that loses the
/// last twenty lines loses precisely the ones worth reading.
/// </summary>
public sealed class SessionLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter? _writer;

    private SessionLog(StreamWriter? writer, string path, string? problem)
    {
        _writer = writer;
        Path = path;
        Problem = problem;
    }

    /// <summary>Where the log is being written, for the UI to show.</summary>
    public string Path { get; }

    /// <summary>
    /// Non-null when the log could not be opened. Nothing about that stops a
    /// session, but the user should know their session is not being recorded
    /// before they rely on it having been.
    /// </summary>
    public string? Problem { get; }

    /// <summary>
    /// Opens a log for this run. One file per run rather than one appended
    /// forever: a session is the unit anybody wants to read, and a file named
    /// for its start time is the easiest possible way to find the right one.
    /// </summary>
    public static SessionLog Create(string? directory = null)
    {
        string folder = directory ?? DefaultDirectory();
        string path = System.IO.Path.Combine(
            folder,
            $"session-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log");

        try
        {
            Directory.CreateDirectory(folder);
            var writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };

            var log = new SessionLog(writer, path, null);
            log.Write(LogSeverity.Info, $"free-polar-align session log started {DateTimeOffset.UtcNow:O}.");

            // Immediately after the start line, before anything can go wrong:
            // every later line in this file is only interpretable if you know
            // which build wrote it. See AppVersion for the incident that taught
            // us that.
            log.Write(LogSeverity.Info, AppVersion.Describe());
            return log;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new SessionLog(
                null,
                path,
                $"Could not open a log file at '{path}': {ex.Message}. This session will not be recorded.");
        }
    }

    public static string DefaultDirectory() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".free-polar-align",
        "logs");

    /// <summary>
    /// Records one engine event. Events the narrator considers not worth a line
    /// (the continuous mount-status poll) are skipped here too, so that the file
    /// and the screen tell the same story.
    /// </summary>
    public void Record(EngineEvent engineEvent)
    {
        ArgumentNullException.ThrowIfNull(engineEvent);

        NarratedEvent narrated = EngineEventNarrator.Describe(engineEvent);
        if (string.IsNullOrEmpty(narrated.Message))
        {
            return;
        }

        Write(narrated.Severity, narrated.Message, engineEvent.TimestampUtc);
    }

    /// <summary>
    /// Records something that is not an engine event -- startup, a settings
    /// problem, a device the catalogue could not list. These belong in the same
    /// file: they are inputs to whatever the session then reported.
    /// </summary>
    public void Write(LogSeverity severity, string message, DateTimeOffset? timestampUtc = null)
    {
        if (_writer is null)
        {
            return;
        }

        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{(timestampUtc ?? DateTimeOffset.UtcNow).UtcDateTime:yyyy-MM-dd HH:mm:ss.fff}Z {Label(severity)} {message}");

        lock (_gate)
        {
            try
            {
                _writer.WriteLine(line);
            }
            catch (IOException)
            {
                // A full or disconnected disk must not take the session with it.
            }
        }
    }

    private static string Label(LogSeverity severity) => severity switch
    {
        LogSeverity.Warning => "WARN ",
        LogSeverity.Error => "ERROR",
        _ => "INFO ",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
        }
    }
}
