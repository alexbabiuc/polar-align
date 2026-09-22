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
    /// <c>equJ2050 = 3</c>, <c>equB1950 = 4</c>. Any other value (a driver bug,
    /// or a future enum member) maps to <see cref="AscomEquatorialSystem.Other"/>,
    /// which <see cref="AscomMount.SlewToCoordinatesAsync"/> refuses to slew in
    /// rather than guessing at.
    /// </summary>
    public static AscomEquatorialSystem MapEquatorialSystem(int ascomEquatorialSystemValue) => ascomEquatorialSystemValue switch
    {
        1 => AscomEquatorialSystem.Topocentric,
        2 => AscomEquatorialSystem.J2000,
        3 => AscomEquatorialSystem.J2050,
        4 => AscomEquatorialSystem.B1950,
        _ => AscomEquatorialSystem.Other
    };

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

/// <summary>
/// The equatorial system an ASCOM driver says its RA/Dec coordinates are in
/// (<c>ITelescope.EquatorialSystem</c>). Named rather than passed around as the
/// raw ordinal because which system a driver is in decides whether
/// <see cref="AscomMount"/> can drive it at all -- see
/// <see cref="AscomMount.SlewToCoordinatesAsync"/>.
/// </summary>
internal enum AscomEquatorialSystem
{
    /// <summary>equOther, or a value this code does not know.</summary>
    Other,

    /// <summary>equTopocentric -- coordinates of date, what mounts and drivers usually call "JNow".</summary>
    Topocentric,

    /// <summary>equJ2000, the system this project's contracts speak natively.</summary>
    J2000,

    /// <summary>equJ2050.</summary>
    J2050,

    /// <summary>equB1950.</summary>
    B1950
}
