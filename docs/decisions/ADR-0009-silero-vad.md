# ADR-0009: Silero VAD replaces the RMS energy gate

Date: 2026-09-01
Status: accepted
Revises: the fixed 500/32767 RMS threshold inside `LanAsrRecognizer.OnData`.

## Context

In continuous listening mode the LAN whisper recognizer segmented utterances with a plain RMS
energy gate (constants carried from Prosim2FO: threshold 500/32767, 700 ms end-silence,
300 ms min speech, 300 ms pre-roll, 15 s hard cap). Three flight-tested failure modes: cockpit
and engine noise (and FO TTS bleeding into the mic) crossed the energy threshold and produced
false segments that whisper then hallucinated text for (#120's absorb list treats the symptom,
not the cause); quiet short commands never crossed it and were lost; and natural pauses over
700 ms split one sentence into two utterances, neither of which snapped to a command.

Energy cannot distinguish "loud" from "speech". A voice-activity model can.

## Decision

- **Silero VAD v6 (ONNX, MIT) makes the per-frame speech decision**; the segmentation state
  machine around it (pre-roll, min-speech, end-silence, hard cap, PTT-release flush) is kept
  and extracted into `UtteranceSegmenter`, testable without a capture device.
- The frame decision hides behind `ISpeechFrameClassifier` (512-sample frames, Silero's
  contract). `SileroVadClassifier` runs the model on CPU via `Microsoft.ML.OnnxRuntime`
  (one intra-op thread; sub-millisecond per 32 ms frame). `RmsClassifier` reproduces the old
  gate bit-for-bit.
- **RMS stays as the fallback and remains selectable** (`speech.vadEngine`). Any Silero
  initialisation failure (missing model, native load error) warns once and degrades to RMS
  for the recognizer's lifetime — recognition is never lost because of the VAD. The RMS path
  keeps its proven 700 ms end-silence regardless of the Silero tuning options.
- Hysteresis per Silero's defaults: speech opens at p ≥ `vadThreshold`, trailing silence only
  counts below `vadThreshold − 0.15`; the Silero end-silence default drops to 500 ms.
- The model is **vendored** (`Recognition/Vad/silero_vad.onnx`, provenance in THIRD_PARTY.md)
  and shipped as a content file — never downloaded at runtime (degraded-mode rule).
- Per-utterance diagnostics (`asr.utterance`: durationMs, peakSpeechProb, endReason,
  vadEngine) go to the session JSONL so thresholds are tuned from flight evidence, not guesses.

## Alternatives considered

- **Keep tuning the RMS gate**: every constant traded false accepts against missed quiet
  commands; no constant fixes "loud ≠ speech".
- **whisper.cpp server-side `--vad`**: the Mac-side whisper-server can run Silero itself over
  the posted file. Complementary (it would trim in-segment silence before decoding) but it
  cannot replace client-side segmentation — something local must still decide when an
  utterance starts and ends in the continuous mic stream. A separate Mac-side change, out of
  scope here.
- **WebRTC VAD**: smaller but energy/GMM-based, materially worse on non-speech noise; the
  Silero model is 2.3 MB and the runtime cost is negligible.

## Consequences

- New dependency `Microsoft.ML.OnnxRuntime` (~15 MB native per RID; the installer publish is
  `-r win-x64` so shipped output stays single-RID) and a 2.3 MB vendored model.
- TTS bleed is reduced (the FO's synthetic voice still IS speech — a true fix is suppressing
  segmentation while the arbiter is speaking; `ISpeechControl` already knows, hook deferred).
- `speech.vadThreshold` / `vadEndSilenceMs` are on the Speech settings page; min-speech,
  pre-roll and the hard cap stay settings.json-only, named in the page hint.
- The offline System.Speech engine is untouched — it has its own engine-internal VAD.
