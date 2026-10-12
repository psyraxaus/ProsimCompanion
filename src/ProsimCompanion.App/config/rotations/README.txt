ProsimCompanion - planned rotations
===================================

Put one JSON file per planned duty day in this folder.

When a company day starts, the app reads the files in name order
(01-..., 02-..., ...) and uses the FIRST one that has at least one leg.
Files that do not parse are skipped. Keep one file here, or name the
file you want to fly so it sorts first.

No file here = the day runs in "progressive" mode: each sector you fly
is added as you go. Nothing is lost.

Example: 2026-10-13-ekch-egll.json

{
  "dayId": "2026-10-13",
  "reportTimeUtc": "2026-10-13T05:30:00Z",
  "legs": [
    { "from": "EKCH", "to": "EGLL", "flightNo": "SAS501",
      "scheduledOffUtc": "2026-10-13T06:30:00Z",
      "scheduledOnUtc":  "2026-10-13T08:25:00Z" },
    { "from": "EGLL", "to": "EKCH", "flightNo": "SAS502",
      "scheduledOffUtc": "2026-10-13T09:30:00Z",
      "scheduledOnUtc":  "2026-10-13T11:20:00Z" }
  ]
}

Fields
  dayId            your label for the day (optional)
  reportTimeUtc    duty start, UTC (optional)
  legs             one entry per sector, in order
    from, to       ICAO codes
    flightNo       as the FO should say it (optional)
    scheduledOffUtc / scheduledOnUtc
                   planned off-blocks and on-blocks, UTC

Times are UTC in the full form YYYY-MM-DDTHH:MM:SSZ. A bare time such
as 06:30 is not understood.

Flown legs are matched against this plan. A different airport or a
late block shows as a deviation in the day summary and the debrief.

The folder location is a setting: Settings > Voice First Officer >
Company & Debrief > Planned rotations folder (empty = this folder).
This README is replaced when the app updates it; your JSON files are
never touched.
