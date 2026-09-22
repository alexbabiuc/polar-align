using FreePolarAlign.App.Services;
using Xunit;

namespace FreePolarAlign.Tests.App;

/// <summary>
/// The line <see cref="SessionLog"/> writes second, naming the build that wrote
/// the rest of the file.
///
/// What is worth testing here is not the formatting but that the line can never
/// be *absent or empty*: it is diagnostic scaffolding, so the one failure mode
/// that matters is it quietly reporting nothing on the machine where a log is
/// finally being read. Hence the assertions are about a usable answer surviving
/// every "unknown" path rather than about an exact string.
/// </summary>
public class AppVersionTests
{
    [Fact]
    public void Describe_NamesVersionCommitAndBuildTime()
    {
        string description = AppVersion.Describe();

        Assert.Contains("Version ", description);
        Assert.Contains("commit ", description);
        Assert.Contains("built ", description);
    }

    /// <summary>
    /// The version and commit come from attributes, and the build time from a
    /// file stat, any of which can be missing or throw. None of them may take
    /// the line down with them, so every branch has to end in a printable token
    /// -- which is what "no empty field" checks for here without pinning the
    /// values themselves, since they differ for every build.
    /// </summary>
    [Fact]
    public void Describe_LeavesNoFieldEmpty()
    {
        string description = AppVersion.Describe();

        Assert.DoesNotContain("Version ,", description);
        Assert.DoesNotContain("Version (", description);
        Assert.DoesNotContain("commit )", description);
        Assert.DoesNotContain("built .", description);
    }

    /// <summary>
    /// The App project stamps the git commit into the informational version at
    /// build time, and a test run is a build, so the commit must actually be
    /// there -- a broken stamping target would otherwise degrade silently to
    /// "no commit stamped" and only be noticed the next time a log needed to
    /// settle which build produced an error.
    /// </summary>
    [Fact]
    public void Describe_CarriesTheStampedGitCommit()
    {
        string description = AppVersion.Describe();

        Assert.DoesNotContain("no commit stamped", description);
    }
}
