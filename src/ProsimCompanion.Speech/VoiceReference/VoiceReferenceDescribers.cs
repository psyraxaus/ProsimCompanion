using ProsimCompanion.Core.VoiceReference;

namespace ProsimCompanion.Speech.VoiceReference;

/// <summary>One described row: the phrase keys as the feature contributes them (a key also
/// matches live phrases that start with it plus a space — the value-parsed seeds and the
/// per-airport logbook phrases), plus the human wording.</summary>
internal sealed record EntryDescriber(
    string[] Phrases,
    string What,
    string? Answer = null,
    string[]? Badges = null,
    string? ValueHint = null,
    VoiceSpeaker? Speaker = null);

/// <summary>How one voice feature type appears in the drawer.</summary>
internal sealed record FeatureDescriber(
    string Id,
    string Title,
    VoiceReferenceTab Tab,
    VoiceSpeaker Speaker,
    string? DisabledReason,
    EntryDescriber[] Entries);

/// <summary>
/// The wording table behind the Voice Reference drawer (issue #136), keyed by feature type.
/// Phrases here are DESCRIPTIONS of the live grammar, never its source: the builder keeps a
/// row only while the feature still contributes its phrases, and any live phrase this table
/// does not know is still shown (undescribed). Four features are composed from their own
/// live data instead (GSX bindings, hails, commands.json, atc-requests.json) and have no
/// entry here — see <see cref="VoiceReferenceBuilder"/>.
/// </summary>
internal static class VoiceReferenceDescribers
{
    public const string Pf = "PF";
    public const string Int = "INT";
    public const string Cab = "CAB";
    public const string Phase = "PHASE";
    public const string Value = "VALUE";
    public const string Dialogue = "DIALOGUE";

    private const string SettingsFo = "Settings → Voice First Officer";

    public static IReadOnlyDictionary<Type, FeatureDescriber> ByFeatureType { get; } =
        new Dictionary<Type, FeatureDescriber>
        {
            [typeof(Roles.RoleManager)] = new(
                "roles", "Controls handover", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer, null,
                [
                    new(["you have control", "your aircraft", "your controls", "you have the aircraft"],
                        "Hand the FO the aircraft. Airborne, the FO asks you to confirm first.", "I have control."),
                    new(["confirm control", "control confirmed", "confirm i have control"],
                        "Confirm an airborne handover.", "I have control."),
                    new(["my aircraft", "i have control", "i have the aircraft", "i have the controls"],
                        "Take the aircraft back — instant, also aborts any pending FCU or MCDU action.", "You have control."),
                    new(["one hundred knots", "hundred knots"], "Your pilot-monitoring call while the FO flies.", "Checked.", [Pf]),
                    new(["positive climb", "positive rate"], "Your pilot-monitoring call while the FO flies.", "Gear up.", [Pf]),
                ]),

            [typeof(Fcu.FcuExecutor)] = new(
                "fcu", "FCU — while the FO is pilot flying", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer, null,
                [
                    new(["set heading", "turn left heading", "turn right heading", "fly heading"],
                        "Set the heading. The FO reads it back, waits 3 s for 'negative', then sets and verifies.",
                        "Setting heading one two zero.", [Pf, Value], "‹one two zero›"),
                    new(["climb flight level", "descend flight level", "maintain flight level"],
                        "Set the altitude as a flight level.", "Setting flight level three five zero.", [Pf, Value], "‹three five zero›"),
                    new(["reduce speed", "increase speed", "maintain speed"],
                        "Set the speed (100–399 kt).", "Setting speed two one zero knots.", [Pf, Value], "‹two one zero›"),
                    new(["vertical speed"], "Set V/S. Say 'descend' or 'down' for a descent.", null, [Pf, Value], "‹one thousand five hundred›"),
                    new(["managed speed", "selected speed"], "Speed managed or selected.", null, [Pf]),
                    new(["open descent", "managed descent", "expedite"], "Altitude knob pull, push, or EXPED.", null, [Pf]),
                    new(["engage autopilot", "engage autopilot one", "engage autopilot two", "engage autothrust"],
                        "AP1, AP2 or A/THR.", null, [Pf]),
                    new(["arm approach", "arm localizer"], "APPR or LOC.", null, [Pf]),
                    new(["resume own navigation"], "Heading back to managed.", null, [Pf]),
                    new(["negative", "disregard", "cancel", "belay that"],
                        "Cancel a pending FCU or radio action inside its 3 s window.", "Disregard."),
                ]),

            [typeof(Radios.RadioExecutor)] = new(
                "radios", "Radios", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer, null,
                [
                    new(["set box one standby", "set box two standby", "set standby", "tune box one", "tune box two", "tune standby"],
                        "Tune a standby frequency (118.000–136.990); the FO reads it back, then sets and verifies.",
                        "Setting box one standby one one eight decimal one.", [Value], "‹one one eight decimal one›"),
                    new(["standby box one", "standby box two"], "Tune that box's standby.", null, [Value], "‹one one eight decimal one›"),
                    new(["swap", "swap box one", "swap box two", "flip", "flip box one", "flip box two"],
                        "Swap active and standby."),
                ]),

            [typeof(Callouts.EngineFlapCallFeature)] = new(
                "engineFlap", "Engine & flap calls", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
                $"speech.engineFlapCallouts is off — {SettingsFo}",
                [
                    new(["starting engine one", "start engine one", "engine one start"], "Engine start acknowledgement.", "Engine one."),
                    new(["starting engine two", "start engine two", "engine two start"], "Engine start acknowledgement.", "Engine two."),
                    new(["flaps one", "set flaps one"], "Placard speed check — nothing is moved.", "Speed checked, flaps one."),
                    new(["flaps two", "set flaps two"], "Placard speed check.", "Speed checked, flaps two."),
                    new(["flaps three", "set flaps three"], "Placard speed check.", "Speed checked, flaps three."),
                    new(["flaps full", "set flaps full"], "Placard speed check.", "Speed checked, flaps full."),
                    new(["flaps up", "flaps zero", "set flaps up"], "Flap retraction acknowledgement.", "Flaps up."),
                ]),

            [typeof(Callouts.GearCallFeature)] = new(
                "gear", "Gear", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer, null,
                [
                    new(["gear up"], "The FO moves the lever — refused on the ground or above the retraction limit.", "Gear up."),
                    new(["gear down"], "The FO moves the lever — refused above the extension limit.", "Gear down."),
                ]),

            [typeof(Callouts.ChronoCallFeature)] = new(
                "chrono", "Takeoff", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer, null,
                [
                    new(["takeoff", "take off", "start the clock"], "Starts the FO chrono on the takeoff roll (stops at touchdown).", "Takeoff.", [Phase]),
                ]),

            [typeof(Callouts.PilotAnnouncementFeature)] = new(
                "announcements", "Flight-mode calls", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer, null,
                [
                    new(["man flex srs runway nav blue", "man toga srs runway nav blue", "man flex", "man toga"],
                        "Takeoff FMA read.", "Checked.", [Phase]),
                    new(["localizer star", "loc star", "glideslope star", "glide slope star", "lockstar", "locstar", "glidestar",
                         "localizer capture", "loc capture", "glideslope capture", "glide slope capture"],
                        "Localizer / glideslope capture call.", "Checked.", [Phase]),
                    new(["alt star", "altstar", "altitude star", "alt capture"], "Altitude capture call.", "Checked.", [Phase]),
                    new(["manual flight", "manual control"], "Manual flight announcement.", "Manual flight."),
                    new(["continue"], "Your decision at minimums.", "Continue.", [Phase]),
                ]),

            [typeof(Mcdu.McduReader)] = new(
                "mcduRead", "MCDU — read", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
                $"mcdu.enabled is off — {SettingsFo} → MCDU",
                [
                    new(["read the mcdu", "read the m c d u", "read the box", "read me the mcdu", "what's on the mcdu", "read the fms"],
                        "The FO reads the current MCDU page aloud."),
                    new(["read the scratchpad", "read the scratch pad", "what's in the scratchpad"], "Reads only the scratchpad."),
                ]),

            [typeof(Mcdu.McduRadNavTuner)] = new(
                "mcduRadNav", "MCDU — ILS", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
                $"mcdu.enabled is off — {SettingsFo} → MCDU",
                [
                    new(["tune the ils", "set the ils", "set up the ils", "tune the radios", "set the radios", "tune the localizer", "tune radio nav"],
                        "The FO tunes the arrival ILS on RAD NAV.", null, [Pf]),
                    new(["negative", "disregard", "cancel", "belay that"], "Abort the tuning."),
                ]),

            [typeof(Mcdu.McduArrivalChanger)] = new(
                "mcduArrival", "MCDU — arrival runway", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
                $"mcdu.enabled is off — {SettingsFo} → MCDU",
                [
                    new(["change arrival runway", "change to runway"],
                        "The FO changes the arrival runway and ILS (LAT REV → ARRIVAL → insert).", null, [Pf, Value], "‹zero four left›"),
                ]),

            [typeof(Briefings.BriefingService)] = new(
                "briefings", "Briefings", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer, null,
                [
                    new(["brief departure", "departure briefing", "brief the departure", "departure brief", "run departure brief",
                         "run the departure brief", "run the departure briefing", "do the departure briefing"],
                        "Departure briefing from the FMS plan, navdata and live weather."),
                    new(["brief arrival", "arrival briefing", "brief the arrival", "arrival brief", "run arrival brief",
                         "run the arrival brief", "run the arrival briefing", "do the arrival briefing", "approach briefing"],
                        "Arrival briefing — the FO asks you to confirm the minimums first.", "Confirm the approach minimums.", [Dialogue]),
                    new(["which approach", "approach options", "confirm approach", "what approach for landing"],
                        "The navdata approaches for the arrival runway."),
                ]),

            [typeof(Briefings.MissedApproachVoiceFeature)] = new(
                "missedApproach", "Missed approach", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer, null,
                [
                    new(["missed approach brief", "missed approach briefing", "brief the missed approach"], "Re-brief the missed approach."),
                ]),

            [typeof(Briefings.MinimaQueryVoiceFeature)] = new(
                "minima", "Minimums", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer, null,
                [
                    new(["what are our minimums", "what's our minimums", "say minimums", "minimums check"],
                        "The minima you confirmed in the arrival briefing.", "Minimums not briefed."),
                ]),

            [typeof(Company.CompanyChannelService)] = new(
                "company", "Company", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.Company, null,
                [
                    new(["request loadsheet", "loadsheet please", "read the loadsheet", "loadsheet"],
                        "Spoken loadsheet from the live weights, pax and CG (ACARS chime)."),
                    new(["read last company message", "repeat company message", "last company message", "say again company"],
                        "Repeats the last company message."),
                ]),

            [typeof(Day.CompanyDayService)] = new(
                "day", "Duty day", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer, null,
                [
                    new(["start duty day", "begin duty day", "start the duty day"], "Starts the company duty day."),
                    new(["end duty day", "close duty day", "end the duty day"], "Ends it with a summary and the logbook fold."),
                ]),

            [typeof(Logbook.LogbookVoiceService)] = new(
                "logbook", "Logbook", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer, null,
                [
                    new(["logbook summary", "read my logbook"], "Your logbook totals."),
                    new(["how many landings", "how many hours", "how many flights"], "One figure."),
                    new(["logbook day summary"], "The last duty day."),
                    new(["landing stats for"], "Landings at one airport.", null, [Value], "‹egll›"),
                ]),

            [typeof(TechLog.TechLogVoiceService)] = new(
                "techLog", "Tech log", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
                $"techLog.enabled is off — {SettingsFo}",
                [
                    new(["tech log", "read the tech log", "tech log brief", "brief tech log", "brief the tech log", "any open items", "open items"],
                        "Reads the open defects."),
                ]),

            [typeof(TechLog.TechLogDialogueService)] = new(
                "techLogDialogue", "Tech log — raise / rectify", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
                $"techLog.enabled is off — {SettingsFo}",
                [
                    new(["log a defect", "raise a defect", "enter a defect", "log a snag"],
                        "Guided: the defect, then the MEL category (bravo, charlie, delta), then confirm.", "Go ahead with the defect.", [Dialogue]),
                    new(["maintenance complete", "maintenance performed", "defect rectified", "clear the tech log", "rectify the defect"],
                        "Guided: 'Rectify …? Affirm or negative.'", null, [Dialogue]),
                ]),

            [typeof(Debrief.DebriefService)] = new(
                "debrief", "Debrief", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer, null,
                [
                    new(["debrief", "debrief now", "flight debrief", "post flight debrief", "give me the debrief"], "Spoken post-flight debrief."),
                ]),

            [typeof(Persona.SmallTalkService)] = new(
                "quiet", "Quiet", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer, null,
                [
                    new(["quiet please", "quiet cockpit", "pipe down", "less chat", "keep it quiet"],
                        "Silences chatter for the rest of the session (checklists and callouts still speak).", "Righto, I'll keep it quiet."),
                ]),

            [typeof(Crew.AircraftStateVoiceService)] = new(
                "aircraftState", "Aircraft state", VoiceReferenceTab.Ground, VoiceSpeaker.FirstOfficer,
                "GSX pillar not running",
                [
                    new(["check the aircraft state", "recheck the aircraft state", "check aircraft state", "recheck aircraft state", "check the state of the aircraft"],
                        "Re-runs the cold-and-dark check; the FO reads the verdict."),
                ]),
        };
}
