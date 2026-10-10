using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Sim;
using ProsimCompanion.Speech.SayIntentions;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The SayIntentions PTT relay's pure parts (2026-10-10): switch edges, the
/// transmit echo, the source → dataref map and the exact-name write allow-list.</summary>
public sealed class SayIntentionsPttRelayTests
{
    [Fact]
    public void Tracker_EmitsOneEdgePerChange_AndIgnoresRepeats()
    {
        var tracker = new PttEdgeTracker();

        Assert.Equal(PttEdgeTracker.Edge.None, tracker.Observe(0));      // released at start: nothing to send
        Assert.Equal(PttEdgeTracker.Edge.Pressed, tracker.Observe(1));
        Assert.Equal(PttEdgeTracker.Edge.None, tracker.Observe(1));      // ProSim re-push of the same value
        Assert.Equal(PttEdgeTracker.Edge.Released, tracker.Observe(0));
        Assert.Equal(PttEdgeTracker.Edge.None, tracker.Observe(0));
    }

    [Fact]
    public void Tracker_ButtonHeldAtStartup_CountsAsAPress()
    {
        var tracker = new PttEdgeTracker();
        Assert.Equal(PttEdgeTracker.Edge.Pressed, tracker.Observe(1));
        Assert.True(tracker.Pressed);
    }

    [Fact]
    public void Tracker_RemembersTheTransmitEcho_OnlyWhileHeld()
    {
        var tracker = new PttEdgeTracker();
        tracker.NoteRadioPtt(1);                 // SI transmitting for some other reason
        tracker.Observe(1);
        Assert.False(tracker.SawTransmit);       // a new press starts clean

        tracker.NoteRadioPtt(1);
        Assert.True(tracker.SawTransmit);
        tracker.Observe(0);
        Assert.True(tracker.SawTransmit);        // still readable for the release event

        tracker.Observe(1);
        Assert.False(tracker.SawTransmit);
        tracker.NoteRadioPtt(0);
        Assert.False(tracker.SawTransmit);
    }

    [Theory]
    [InlineData("captainSidestick", "system.switches.S_SIDESTICK_PTT_CAPT")]
    [InlineData("foSidestick", "system.switches.S_SIDESTICK_PTT_FO")]
    [InlineData("captainHandMic", "system.switches.S_HAND_MIC_PTT_CAPT")]
    [InlineData("FOHANDMIC", "system.switches.S_HAND_MIC_PTT_FO")]
    [InlineData("observerHandMic", "system.switches.S_HAND_MIC_PTT_OBS")]
    public void SourceRef_MapsTheSettingsKeyToTheCatalogSwitch(string key, string expected)
    {
        var dataRef = SayIntentionsPttRelay.SourceRef(key);
        Assert.NotNull(dataRef);
        Assert.Equal(expected, dataRef.Value.Name);
        Assert.Equal(DataRefTier.Critical, dataRef.Value.Tier);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("joystick")]
    public void SourceRef_IsNullForOffOrUnknown(string? key)
    {
        Assert.Null(SayIntentionsPttRelay.SourceRef(key));
    }

    [Theory]
    [InlineData("L:SIAI_CONTROL_PTT_COM", true)]
    [InlineData("L:SIAI_CONTROL_PTT_COM1", true)]
    [InlineData("L:SIAI_CONTROL_PTT_INTERCOM3", true)]
    [InlineData("L:SIAI_CONTROL_PTT_GROUP", true)]
    [InlineData("l:siai_control_ptt_com", true)]          // the sim is case-insensitive; so is the gate
    [InlineData("L:SIAI_CONTROL_PTT_COMX", false)]        // exact names, not a prefix
    [InlineData("L:SIAI_RADIO_PTT", false)]               // SI's read-only status flag stays read-only
    [InlineData("L:SIAI_ATC_MASTER_OFF", false)]
    public void SimWriteGate_AllowsExactlyTheSevenControlLvars(string name, bool allowed)
    {
        Assert.Equal(allowed, SimWriteGate.IsAllowed(name));
    }

    [Fact]
    public void ControlLvarList_MatchesTheWriteGate()
    {
        foreach (var (name, _) in SayIntentionsLvarNames.ControlPttLvars)
        {
            Assert.True(SimWriteGate.IsAllowed(name), name);
        }
    }
}
