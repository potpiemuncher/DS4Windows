using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class NativeModeLogClassifierTests
{
    [DataTestMethod]
    [DataRow("USB/IP server listening on 127.0.0.1:3240; busid 1-1; input Bluetooth DualSense.",
        NativeModeLogKind.ServerListening)]
    [DataRow("No physical Bluetooth DualSense with streaming reports was found.",
        NativeModeLogKind.PadOpenFailure)]
    [DataRow("Bluetooth input stopped after 750 valid reports (Win32 error 1167). The physical pad is gone; restart serve after it reconnects.",
        NativeModeLogKind.PadLost)]
    [DataRow("14:10:22.419 ISO OUT total=500 seq=28610 ep=1 urbs/s=98.7 KiB/s=370.1 packets=5000 current=10x384..384 gap-ms=9.1..17.0 rms%=0.00/0.00/15.90/15.90 peak%=0.0/0.0/35.0/35.0 start=5000 interval=1 bt36=461 btq=0 bt-underrun=90 bt-errors=0 bt-audio=420 spkq=5 spk-underrun=0",
        NativeModeLogKind.IsochronousOutStats)]
    [DataRow("14:10:25.000 AUDIO bt-audio=420 spkq=5 spk-underrun=0 spk-dropped=0 mic-rep=0 mic-rate=measuring micq=0 mic-underrun=0 mic-KiB=0.0 mic-decode-err=0 bt-errors=0",
        NativeModeLogKind.AudioStats)]
    [DataRow("Speaker rebuffer #3: ring=3840 samples, iso-age=17.2 ms, since-last=1010.5 ms.",
        NativeModeLogKind.SpeakerRebuffer)]
    public void Classify_RecognizesRealServerMarkers(string line, NativeModeLogKind expected)
    {
        Assert.AreEqual(expected, NativeModeLogClassifier.Classify(line));
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("Virtual DualSense (composite configuration) is ready for usbip-win2.")]
    [DataRow("19:09:52.504 HID OUT total=1 seq=28614 via=interrupt-out bytes=48 020D1700")]
    public void Classify_IgnoresUnrelatedLines(string line)
    {
        Assert.AreEqual(NativeModeLogKind.Other, NativeModeLogClassifier.Classify(line));
    }
}
