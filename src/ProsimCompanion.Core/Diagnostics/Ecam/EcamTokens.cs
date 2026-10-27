namespace ProsimCompanion.Core.Diagnostics.Ecam;

/// <summary>
/// The avionics-style tokens the diagnostics pages print. They are ECAM grammar, not prose,
/// and are deliberately NOT in the language packs (ADR-0014 exception): a ProSim user reads
/// <c>XX</c> and <c>- - -</c> the same way in every language, and translating them would make
/// the page disagree with the aircraft's own displays.
/// </summary>
public static class EcamTokens
{
    /// <summary>Value of a node whose data source is missing: amber.</summary>
    public const string NoData = "XX";

    /// <summary>Value of a node that is intentionally not in use: white.</summary>
    public const string NotApplicable = "- - -";

    public const string Conn = "CONN";
    public const string Reconn = "RECONN";
    public const string Off = "OFF";
    public const string On = "ON";
    public const string Idle = "IDLE";
    public const string Playing = "PLAYING";
    public const string Speech = "SPEECH";
    public const string Silence = "SILENCE";
    public const string Avail = "AVAIL";
    public const string Req = "REQ";
    public const string Prog = "PROG";
    public const string Done = "DONE";
    public const string NotReq = "NOT REQ";
    public const string Auto = "AUTO";
    public const string Sent = "SENT";
    public const string HoldLast = "HOLD LAST";
    public const string Muted = "MUTED";
    public const string Lost = "LOST";
    public const string Ptt = "PTT";
    public const string OnGround = "ON GND";
    public const string Airborne = "AIRBORNE";
    public const string NoResp = "NO RESP";
    public const string Cached = "CACHED";
    public const string Unavail = "UNAVAIL";

    /// <summary>PANELS page words (Skalarki hardware, 2026-10-27).</summary>
    public const string LinkUp = "LINK UP";
    public const string LinkDown = "LINK DOWN";
    public const string LampTest = "LAMP TEST";
    public const string Restart = "RESTART";
    public const string Start = "START";
    public const string Stop = "STOP";
    public const string HwCheck = "HW CHECK";
    public const string HwReset = "HW RESET";
    public const string Confirm = "CONFIRM?";
    public const string Pending = "PENDING";
    public const string Skipped = "SKIPPED";
    public const string Complete = "COMPLETE";
    public const string Last = "LAST";
    public const string Evt = "EVT";
    public const string Devices = "DEVICES";
    public const string Profiler = "PROFILER";
    public const string Link = "LINK";

    /// <summary>Unit qualifiers (cyan on the page).</summary>
    public static class Units
    {
        public const string Ms = "MS";
        public const string Hz = "HZ";
        public const string Db = "DB";
        public const string Gb = "GB";
        public const string Percent = "%";
        public const string Tonnes = "T";
        public const string Seconds = "S";
        public const string Khz = "KHZ";
    }
}
