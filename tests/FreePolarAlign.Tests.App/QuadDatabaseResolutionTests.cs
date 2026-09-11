using FreePolarAlign.App.Services;
using Xunit;

namespace FreePolarAlign.Tests.App;

/// <summary>
/// Where the app looks for the plate-solver's quad database (D13),
/// in order: an explicit override, a copy bundled next to the executable,
/// then the default per-user install location.
///
/// The bundled case is what makes a fully offline distribution possible -- a
/// USB stick with the app and its ~790 MB of index files together, no setup
/// step, no environment variable to remember to set. Getting the *order*
/// wrong would be the kind of bug that only shows up on the one machine that
/// has no internet access to diagnose it with, so the precedence is pinned
/// down here explicitly rather than only by having been written once.
/// </summary>
public class QuadDatabaseResolutionTests
{
    // Built with Path.Combine rather than written as literal strings, so the
    // expectations use whatever directory separator this test happens to run
    // under -- the precedence being tested has nothing to do with which
    // character separates a path, and hardcoding one would make the test fail
    // on a platform other than the one it was written against for a reason
    // that has nothing to do with the logic it is meant to check.
    private static readonly string AppDirectory = Path.Combine("C:", "FreePolarAlign");
    private static readonly string BundledDirectory = Path.Combine(AppDirectory, "quaddb");
    private static readonly string UserProfile = Path.Combine("C:", "Users", "someone");
    private static readonly string DefaultDirectory = Path.Combine(UserProfile, ".free-polar-align", "quaddb");

    private static string Resolve(string? environmentOverride, bool bundledIsUsable) =>
        EngineFactory.ResolveQuadDatabaseDirectory(
            environmentOverride,
            AppDirectory,
            UserProfile,
            directory => directory == BundledDirectory && bundledIsUsable);

    /// <summary>
    /// The whole point of the offline build: a database sitting next to the
    /// executable is found with no configuration at all.
    /// </summary>
    [Fact]
    public void ABundledDatabaseNextToTheExecutable_IsUsedWithNoConfiguration()
    {
        Assert.Equal(BundledDirectory, Resolve(environmentOverride: null, bundledIsUsable: true));
    }

    /// <summary>
    /// An explicit override wins even when a bundled copy would otherwise be
    /// used -- someone who has set FPA_QUADDB_DIR made a deliberate choice, and
    /// a bundled copy silently taking priority over it would be the more
    /// surprising behaviour, not the safer one.
    /// </summary>
    [Fact]
    public void AnExplicitOverride_WinsEvenOverABundledDatabase()
    {
        string overridden = Path.Combine("D:", "somewhere-else");
        Assert.Equal(overridden, Resolve(environmentOverride: overridden, bundledIsUsable: true));
    }

    [Fact]
    public void AnExplicitOverride_WinsWhenThereIsNoBundledDatabaseEither()
    {
        string overridden = Path.Combine("D:", "somewhere-else");
        Assert.Equal(overridden, Resolve(environmentOverride: overridden, bundledIsUsable: false));
    }

    /// <summary>
    /// With no override and nothing bundled, an ordinary installed build falls
    /// back to the per-user default -- the behaviour this project had before
    /// the offline distribution existed, and the one every non-offline install
    /// still relies on.
    /// </summary>
    [Fact]
    public void WithNothingBundledAndNoOverride_FallsBackToTheUserProfileDefault()
    {
        Assert.Equal(DefaultDirectory, Resolve(environmentOverride: null, bundledIsUsable: false));
    }

    /// <summary>
    /// An override of whitespace is treated as absent, matching how every other
    /// environment-variable read in this project (e.g. settings, logging) is
    /// forgiving of an accidentally-set-but-empty variable rather than trying
    /// to use "" or "   " as a literal path.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankOverride_IsTreatedAsAbsent(string blank)
    {
        Assert.Equal(BundledDirectory, Resolve(environmentOverride: blank, bundledIsUsable: true));
        Assert.Equal(DefaultDirectory, Resolve(environmentOverride: blank, bundledIsUsable: false));
    }

    /// <summary>
    /// The bundled path is always checked directly under the application
    /// directory -- not, say, one level up or in a fixed absolute path -- which
    /// is what lets the whole folder be moved (to a USB stick, a different
    /// drive letter) and keep working.
    /// </summary>
    [Fact]
    public void TheBundledPathIsRelativeToWhereverTheApplicationActuallyRuns()
    {
        string movedAppDirectory = Path.Combine("E:", "usb-stick", "FreePolarAlign");
        string expectedBundled = Path.Combine(movedAppDirectory, "quaddb");

        string result = EngineFactory.ResolveQuadDatabaseDirectory(
            environmentOverride: null,
            applicationDirectory: movedAppDirectory,
            userProfileDirectory: UserProfile,
            hasUsableQuadDatabase: directory => directory == expectedBundled);

        Assert.Equal(expectedBundled, result);
    }
}
