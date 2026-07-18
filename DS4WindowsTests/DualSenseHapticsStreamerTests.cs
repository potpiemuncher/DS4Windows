using DS4Windows.InputDevices;

namespace DS4WindowsTests;

[TestClass]
public class DualSenseHapticsStreamerTests
{
    [DataTestMethod]
    [DataRow((byte)0)]
    [DataRow(DualSenseHapticsStreamer.RUMBLE_SYNTH_DEADZONE)]
    public void ScaleRumbleStrength_SuppressesNoiseFloor(byte strength)
    {
        Assert.AreEqual(0.0, DualSenseHapticsStreamer.ScaleRumbleStrength(strength));
    }

    [TestMethod]
    public void ScaleRumbleStrength_PreservesFullStrength()
    {
        Assert.AreEqual(1.0, DualSenseHapticsStreamer.ScaleRumbleStrength(byte.MaxValue));
    }

    [TestMethod]
    public void ScaleRumbleStrength_RescalesValuesAboveNoiseFloor()
    {
        byte strength = (byte)(DualSenseHapticsStreamer.RUMBLE_SYNTH_DEADZONE + 1);
        double expected = 1.0 /
            (byte.MaxValue - DualSenseHapticsStreamer.RUMBLE_SYNTH_DEADZONE);

        Assert.AreEqual(expected, DualSenseHapticsStreamer.ScaleRumbleStrength(strength), 1e-12);
    }

    [TestMethod]
    public void HasHapticSignal_TreatsCenteredChunkAsSilence()
    {
        byte[] chunk = Enumerable.Repeat((byte)0x80, 64).ToArray();

        Assert.IsFalse(DualSenseHapticsStreamer.HasHapticSignal(chunk));
    }

    [TestMethod]
    public void HasHapticSignal_DetectsSampleAwayFromCenter()
    {
        byte[] chunk = Enumerable.Repeat((byte)0x80, 64).ToArray();
        chunk[31] = 0x81;

        Assert.IsTrue(DualSenseHapticsStreamer.HasHapticSignal(chunk));
    }
}
