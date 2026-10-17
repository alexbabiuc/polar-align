namespace FreePolarAlign.Tests.Support;

/// <summary>
/// Whether this run records what the tests replay (D38): rendered frames,
/// scenario frames and catalogue downloads. Off, a test that finds nothing kept
/// fails and says to record, rather than quietly rendering or downloading.
/// </summary>
public static class FrameRecording
{
    public const string Variable = "FPA_RECORD_FRAMES";

    public static bool Enabled => Environment.GetEnvironmentVariable(Variable) == "1";

    public static string HowToRecord => $"Record it with {Variable}=1.";
}
