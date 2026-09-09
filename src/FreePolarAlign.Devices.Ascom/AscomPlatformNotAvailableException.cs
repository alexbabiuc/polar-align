namespace FreePolarAlign.Devices.Ascom;

/// <summary>
/// Thrown whenever a required ASCOM COM component (the Profile object used
/// for discovery, or a specific driver ProgID) cannot be created. D4 is
/// explicit that a missing ASCOM Platform must produce a readable message,
/// never an empty device list or a bare <see cref="System.Runtime.InteropServices.COMException"/>
/// whose HRESULT means nothing to a user. Every catch site in this project
/// that touches ASCOM COM objects funnels failures through this type with a
/// plain-language explanation and (where known) a pointer to the fix.
/// </summary>
public sealed class AscomPlatformNotAvailableException : Exception
{
    public AscomPlatformNotAvailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
