using FreePolarAlign.Core.Alignment;
using FreePolarAlign.Devices;
using FreePolarAlign.Devices.Simulated;
using FreePolarAlign.Devices.Simulated.SyntheticSky;
using FreePolarAlign.Imaging.Fits;
using Xunit;

namespace FreePolarAlign.Tests.Devices;

/// <summary>
/// The simulated camera's gain control. It exists so that D25's guarantee --
/// gain can change mid-sequence and the next frame says so -- can be tested
/// end to end without a native camera, which is why what is checked here is
/// the header rather than the pixels.
/// </summary>
public class SimulatedCameraGainTests
{
    private static readonly GeodeticLocation Site = new(45.0, 15.0, 200.0);

    /// <summary>Small and starless: only the header is under test, and a full frame takes a second to render.</summary>
    private static readonly SimulatedCameraOptions Tiny = new(WidthPixels: 64, HeightPixels: 48);

    private static (SimulatedMount Mount, SimulatedCamera Camera) Open()
    {
        var mount = new SimulatedMount(new SimulatedMountOptions(Site, MountMisalignment.Aligned));
        var camera = new SimulatedCamera(mount, new StarCatalog(Array.Empty<CatalogStar>()), Tiny);
        return (mount, camera);
    }

    [Fact]
    public async Task Gain_is_offered_once_connected_and_clamped_to_its_range()
    {
        (SimulatedMount mount, SimulatedCamera camera) = Open();
        using (mount)
        using (camera)
        {
            // As the native cameras do: their range is read from the device.
            Assert.Null(camera.GainRange);
            Assert.Null(camera.Gain);

            await camera.ConnectAsync();
            Assert.Equal(new CameraGainRange(0, 100), camera.GainRange);

            await camera.SetGainAsync(250);
            Assert.Equal(100, camera.Gain);

            await camera.SetGainAsync(-5);
            Assert.Equal(0, camera.Gain);
        }
    }

    /// <summary>
    /// The exit criterion's own shape: a gain change between frames appears in
    /// the next one's header, beside the exposure it was taken at.
    /// </summary>
    [Fact]
    public async Task A_gain_change_appears_in_the_next_frames_header_with_its_exposure()
    {
        (SimulatedMount mount, SimulatedCamera camera) = Open();
        using (mount)
        using (camera)
        {
            await mount.ConnectAsync();
            await camera.ConnectAsync();

            await camera.SetGainAsync(20);
            CapturedImage first = await camera.ExposeAsync(TimeSpan.FromSeconds(0.5));

            await camera.SetGainAsync(75);
            CapturedImage second = await camera.ExposeAsync(TimeSpan.FromSeconds(2.0));

            FitsHeader firstHeader = FitsFile.Read(first.FitsPath).ExtraHeader;
            FitsHeader secondHeader = FitsFile.Read(second.FitsPath).ExtraHeader;

            Assert.Equal(20.0, firstHeader.GetDouble("GAIN"));
            Assert.Equal(75.0, secondHeader.GetDouble("GAIN"));
            Assert.Equal(0.5, firstHeader.GetDouble("EXPTIME"));
            Assert.Equal(2.0, secondHeader.GetDouble("EXPTIME"));
        }
    }

    /// <summary>
    /// Discovery says so too, since that is what decides whether the UI shows
    /// the control before anything is connected.
    /// </summary>
    [Fact]
    public void The_simulated_camera_is_discovered_with_a_gain_control()
    {
        var provider = new SimulatedDeviceProvider(
            new StarCatalog(Array.Empty<CatalogStar>()),
            new SimulatedMountOptions(Site, MountMisalignment.Aligned));

        CameraDescriptor camera = Assert.Single(provider.DiscoverDevices().OfType<CameraDescriptor>());
        Assert.True(camera.HasGainControl);
    }
}
