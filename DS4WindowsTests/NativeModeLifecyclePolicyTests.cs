using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class NativeModeLifecyclePolicyTests
{
    [DataTestMethod]
    [DataRow(NativeModeState.Starting, false, false, true)]
    [DataRow(NativeModeState.Serving, false, false, true)]
    [DataRow(NativeModeState.Attached, false, false, true)]
    [DataRow(NativeModeState.Stopped, true, false, true)]
    [DataRow(NativeModeState.PadLost, true, false, true)]
    [DataRow(NativeModeState.Faulted, false, true, true)]
    [DataRow(NativeModeState.PadLost, false, false, false)]
    [DataRow(NativeModeState.Faulted, false, false, false)]
    [DataRow(NativeModeState.SetupRequired, false, false, false)]
    [DataRow(NativeModeState.Stopped, false, false, false)]
    public void IsSessionActive_TracksManagerTransitionOrSuppressionOwnership(
        NativeModeState state, bool suppressionActive, bool ownsChildProcess,
        bool expected)
    {
        Assert.AreEqual(expected,
            NativeModeLifecyclePolicy.IsSessionActive(state, suppressionActive,
                ownsChildProcess));
    }

    [DataTestMethod]
    [DataRow(NativeModeState.PadLost, true, true)]
    [DataRow(NativeModeState.Faulted, true, true)]
    [DataRow(NativeModeState.PadLost, false, false)]
    [DataRow(NativeModeState.Faulted, false, false)]
    [DataRow(NativeModeState.Attached, true, false)]
    [DataRow(NativeModeState.Stopped, true, false)]
    public void RequiresAutomaticCleanup_OnlyForOwnedTerminalSessions(
        NativeModeState state, bool suppressionActive, bool expected)
    {
        Assert.AreEqual(expected,
            NativeModeLifecyclePolicy.RequiresAutomaticCleanup(
                state, suppressionActive));
    }
}
