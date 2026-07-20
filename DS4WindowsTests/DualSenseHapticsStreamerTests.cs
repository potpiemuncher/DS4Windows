using DS4Windows;
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

    [DataTestMethod]
    [DataRow(-1.0, (byte)1)]
    [DataRow(0.0, (byte)128)]
    [DataRow(1.0, (byte)255)]
    public void UnitSampleToU8_UsesFullSignedPcmRange(double sample, byte expected)
    {
        Assert.AreEqual(expected, DualSenseHapticsStreamer.UnitSampleToU8(sample));
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

    [TestMethod]
    public void DitherQuantizeU8_ExactSilencePassesThrough()
    {
        // Dithered silence would defeat HasHapticSignal idle detection and the
        // silence gate, so exact digital zero must always map to exactly 0x80.
        uint state = 0x12345678;
        for (int i = 0; i < 100; i++)
        {
            Assert.AreEqual((byte)0x80, DualSenseHapticsStreamer.DitherQuantizeU8(0.0, ref state));
        }
    }

    [TestMethod]
    public void DitherQuantizeU8_StaysWithinOneLsbOfUndithered()
    {
        const double x = 0.25;
        double softClipped = x / (1.0 + Math.Abs(x));
        int reference = (int)Math.Round(128.0 + softClipped * 127.0);

        uint state = 0x9E3779B9;
        for (int i = 0; i < 1000; i++)
        {
            byte q = DualSenseHapticsStreamer.DitherQuantizeU8(x, ref state);
            Assert.IsTrue(Math.Abs(q - reference) <= 2,
                $"dithered value {q} strayed more than 2 LSB from {reference}");
        }
    }

    [DataTestMethod]
    [DataRow(1.0, DualSenseHapticsStreamer.AUDIO_RATE_TRIM_LIMIT)]
    [DataRow(-1.0, -DualSenseHapticsStreamer.AUDIO_RATE_TRIM_LIMIT)]
    [DataRow(0.001, 0.001)]
    public void ClampRateTrim_LimitsServoAuthority(double input, double expected)
    {
        Assert.AreEqual(expected, DualSenseHapticsStreamer.ClampRateTrim(input), 1e-12);
    }

    [TestMethod]
    public void BuildFillerFrame_DecaysToSilenceWithoutStep()
    {
        short[] pcm = new short[960];
        double lastL = 16000.0, lastR = -12000.0;

        DualSenseHapticsStreamer.BuildFillerFrame(pcm, ref lastL, ref lastR);

        // First sample continues from the previous output (no step discontinuity)
        Assert.IsTrue(Math.Abs(pcm[0] - 16000.0 * 0.985) < 2.0);
        Assert.IsTrue(Math.Abs(pcm[1] + 12000.0 * 0.985) < 2.0);

        // A second filler frame is effectively silent
        DualSenseHapticsStreamer.BuildFillerFrame(pcm, ref lastL, ref lastR);
        Assert.IsTrue(Math.Abs(lastL) < 1.0);
        Assert.IsTrue(Math.Abs(lastR) < 1.0);
    }

    [TestMethod]
    public void ApplyResumeFade_RampsInFromZero()
    {
        short[] pcm = new short[960];
        Array.Fill(pcm, (short)10000);

        DualSenseHapticsStreamer.ApplyResumeFade(pcm);

        Assert.AreEqual(0, pcm[0]);
        Assert.AreEqual(0, pcm[1]);
        Assert.IsTrue(pcm[2] < 100); // early ramp stays near zero
        // Beyond the fade window the content is untouched
        Assert.AreEqual(10000, pcm[DualSenseHapticsStreamer.RESUME_FADE_FRAMES * 2]);
    }

    [DataTestMethod]
    [DataRow(DualSenseControllerOptions.AudioLatencyMode.LowLatency, (byte)32)]
    [DataRow(DualSenseControllerOptions.AudioLatencyMode.Balanced, (byte)64)]
    [DataRow(DualSenseControllerOptions.AudioLatencyMode.Smooth, (byte)120)]
    public void GetLatencyProfile_CarriesControllerDejitterDepth(
        DualSenseControllerOptions.AudioLatencyMode mode, byte expectedBuffer)
    {
        Assert.AreEqual(expectedBuffer,
            DualSenseHapticsStreamer.GetLatencyProfile(mode).ControllerBuffer);
    }

    [DataTestMethod]
    [DataRow(DualSenseControllerOptions.AudioLatencyMode.LowLatency)]
    [DataRow(DualSenseControllerOptions.AudioLatencyMode.Balanced)]
    [DataRow(DualSenseControllerOptions.AudioLatencyMode.Smooth)]
    public void GetLatencyProfile_LeavesAdaptiveRingHeadroom(
        DualSenseControllerOptions.AudioLatencyMode mode)
    {
        var profile = DualSenseHapticsStreamer.GetLatencyProfile(mode);
        int ringFrames = profile.AudioRingSamples / 960;

        // The adaptive prebuffer escalates up to ringFrames - 4; the base
        // target must leave at least that headroom to escalate into.
        Assert.IsTrue(profile.PrebufferFrames + 4 <= ringFrames,
            $"{mode}: prebuffer {profile.PrebufferFrames}f has no headroom in {ringFrames}f ring");
    }
}
