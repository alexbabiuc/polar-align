namespace FreePolarAlign.Devices.Ascom;

/// <summary>
/// Pure, COM-free logic pulled out of <see cref="AscomMount"/> and
/// <see cref="AscomCamera"/> specifically so it has a seam this project's
/// unit tests can exercise on any OS. Everything else in this assembly
/// touches a COM object and is therefore compiled-but-unverified here (see
/// the project's test project for what actually runs).
/// </summary>
internal static class AscomMapping
{
    /// <summary>
    /// Maps the ASCOM <c>PierSide</c> enum's ordinal (as returned late-bound
    /// from <c>ITelescope.SideOfPier</c>) to the contract's <see cref="PierSide"/>.
    /// Per the ASCOM Platform's <c>ITelescopeV3</c> interface specification,
    /// <c>PierSide</c> is <c>pierEast = 0</c>, <c>pierWest = 1</c>,
    /// <c>pierUnknown = -1</c>. Any other value (a driver bug, or a future
    /// enum member) also maps to <see cref="Devices.PierSide.Unknown"/> rather
    /// than guessing, matching D9's "treat SideOfPier as advisory" stance.
    /// </summary>
    public static PierSide MapPierSide(int ascomPierSideValue) => ascomPierSideValue switch
    {
        0 => PierSide.East,
        1 => PierSide.West,
        _ => PierSide.Unknown
    };

    /// <summary>
    /// Per the ASCOM Platform's <c>EquatorialCoordinateType</c> enum:
    /// <c>equOther = 0</c>, <c>equTopocentric = 1</c>, <c>equJ2000 = 2</c>,
    /// <c>equJ2050 = 3</c>, <c>equB1950 = 4</c>. Used to guard
    /// <see cref="AscomMount.SlewToCoordinatesAsync"/>, which the
    /// <see cref="IMount"/> contract documents as taking J2000 coordinates --
    /// see that method's doc comment for why a non-J2000 driver cannot safely
    /// be driven through this contract today.
    /// </summary>
    public static bool IsJ2000(int ascomEquatorialSystemValue) => ascomEquatorialSystemValue == 2;

    /// <summary>ASCOM's Telescope/Camera APIs speak right ascension in hours; this contract, and everything upstream of it, speaks degrees.</summary>
    public static double RaHoursToDegrees(double raHours) => raHours * 15.0;

    /// <summary>Inverse of <see cref="RaHoursToDegrees"/>.</summary>
    public static double RaDegreesToHours(double raDegrees) => raDegrees / 15.0;

    /// <summary>
    /// The midpoint of a nominally-<paramref name="commandedDuration"/>-long
    /// exposure that began at <paramref name="startUtc"/>. Deliberately based
    /// on the commanded duration rather than wall-clock elapsed time between
    /// <c>StartExposure</c> and <c>ImageReady</c> becoming true, because that
    /// interval also includes CCD/CMOS readout and USB/network transfer time
    /// which happens strictly after the shutter (or exposure window) closes
    /// and would bias the midpoint late. See <see cref="ICamera"/>'s doc
    /// comment on <c>ExposureMidpointUtc</c> for why the midpoint matters.
    /// </summary>
    public static DateTime ComputeExposureMidpointUtc(DateTime startUtc, TimeSpan commandedDuration) =>
        startUtc + TimeSpan.FromTicks(commandedDuration.Ticks / 2);
}
