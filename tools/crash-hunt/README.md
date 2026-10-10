# Crash hunt scripts

For a hard crash (process gone, no error line in the log). Windows Error Reporting on the sim
PC keeps a dump per crash in `%LOCALAPPDATA%\CrashDumps\ProsimCompanion.exe.<pid>.dmp`.

| Script | Where to run | What it does |
|---|---|---|
| `minidump-exception.ps1 -Path x.dmp` | dev PC | Exception code, faulting thread, faulting address → module. `0xE0434352` = a .NET exception (the app log has the stack); `0xC0000374` = heap corruption (native code wrote past a buffer; the reporting thread is the *finder*, not the culprit). |
| `minidump-stack.ps1 -Path x.dmp [-ThreadId n]` | dev PC | Return-address candidates on a thread's native stack as `module+offset` — enough to see which native components (dinput, winmm, VoicemeeterRemote64, onnxruntime, …) sit on it. No symbols needed. For managed frames use `dotnet-dump analyze x.dmp -c "clrstack -all"`. |
| `enable-page-heap.ps1` | sim PC, **as administrator** | Standard page heap for `ProsimCompanion.exe` (fill patterns + per-block allocation/free stacks) and full WER dumps (max 3). The next heap-corruption dump names the writer. `-Full` for a guard page per allocation (stops at the exact write, much more memory). `-Disable` to undo. Restart the app after either. |

2026-10-10: crash dump `25588` was `0xC0000374` found by the JIT allocating; prime suspect is
DirectInput overrunning inside `joyGetPosEx` during game-controller re-enumeration (same code
and config as the 2026-09-20 crash). Page heap was enabled on the sim PC to prove it.

2026-10-11: dump `26396` (page heap on, 2.3 GB) settled it — `0xC0000005` in coreclr under
`winmm!joyGetPosEx` on the Blazor render thread, called from `SpeechSettings.OnInitialized →
PushToTalkService.GetJoysticks` the moment the Voice FO settings page opened, with NO joystick
binding and the poller idle. Any call into the winmm joystick layer on that PC can kill the
process; the app now enters it only on the pilot's explicit Scan / joystick capture (issue #165).
`minidump-exception.ps1` streams the file since then — `ReadAllBytes` stops at 2 GB.
