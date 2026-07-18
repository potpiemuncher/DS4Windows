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

    [TestMethod]
    public void DownmixToStereo_PreservesStereoSamples()
    {
        float[] samples = { 0.25f, -0.5f };

        DualSenseHapticsStreamer.DownmixToStereo(samples, 0, 2, out float left, out float right);

        Assert.AreEqual(0.25f, left);
        Assert.AreEqual(-0.5f, right);
    }

    [TestMethod]
    public void DownmixToStereo_SharesSevenPointOneCenterChannel()
    {
        float[] samples = { 0.0f, 0.0f, 0.4f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f };
        float expected = MathF.Tanh(0.4f * 0.70710678f);

        DualSenseHapticsStreamer.DownmixToStereo(samples, 0, 8, out float left, out float right);

        Assert.AreEqual(expected, left, 1e-6f);
        Assert.AreEqual(expected, right, 1e-6f);
    }

    [TestMethod]
    public void DownmixToStereo_RoutesSevenPointOneSideChannels()
    {
        float[] samples = { 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.5f, -0.25f };

        DualSenseHapticsStreamer.DownmixToStereo(samples, 0, 8, out float left, out float right);

        Assert.IsTrue(left > 0.0f);
        Assert.IsTrue(right < 0.0f);
        Assert.IsTrue(Math.Abs(left) < 1.0f);
        Assert.IsTrue(Math.Abs(right) < 1.0f);
    }
}
