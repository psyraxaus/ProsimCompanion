"""
ProsimCompanion example speech recognition server (faster-whisper).

Speaks the "Simple (/transcribe)" server type of ProsimCompanion:

  GET  /health      -> {"status": "ok", "model": "<model name>"}
  POST /transcribe  -> multipart form: "file" (WAV, the app sends 16 kHz mono 16-bit),
                       optional "hotwords" (comma-separated phrases the app expects)
                    <- {"text": "...", "confidence": 0.93, "no_speech_prob": 0.01,
                        "duration": 2.5}

"confidence" is exp(mean log-probability of the segments, weighted by their length), 0..1.
"no_speech_prob" is the highest value over the segments. Silence gives an empty text with
confidence 0 and no_speech_prob 1, so the app drops it.

Settings come from environment variables (all optional):
  ASR_MODEL     faster-whisper model name or path      (default: large-v3)
  ASR_DEVICE    auto | cuda | cpu                      (default: auto)
  ASR_COMPUTE   compute type, e.g. float16, int8       (default: default)
  ASR_LANGUAGE  language code; empty = detect          (default: en)

Run:  uvicorn server:app --host 0.0.0.0 --port 8000
"""
import io
import math
import os
import threading

from fastapi import FastAPI, File, Form, UploadFile
from faster_whisper import WhisperModel

MODEL = os.getenv("ASR_MODEL", "large-v3")
DEVICE = os.getenv("ASR_DEVICE", "auto")
COMPUTE = os.getenv("ASR_COMPUTE", "default")
LANGUAGE = os.getenv("ASR_LANGUAGE", "en") or None

app = FastAPI(title="ProsimCompanion example ASR server")

# One model, one sentence at a time: a flight deck has one speaker.
model = WhisperModel(MODEL, device=DEVICE, compute_type=COMPUTE)
busy = threading.Lock()


@app.get("/health")
def health():
    return {"status": "ok", "model": MODEL}


@app.post("/transcribe")
def transcribe(file: UploadFile = File(...), hotwords: str | None = Form(None)):
    audio = io.BytesIO(file.file.read())

    options = {
        "language": LANGUAGE,
        "beam_size": 5,
        "temperature": 0.0,
        # The server's own voice-activity filter removes silence before decoding.
        "vad_filter": True,
        # Every sentence stands alone; text from the sentence before must not leak in.
        "condition_on_previous_text": False,
    }
    if hotwords and hotwords.strip():
        options["hotwords"] = hotwords.strip()

    with busy:
        found, info = model.transcribe(audio, **options)
        segments = list(found)  # the result is lazy: read it while we hold the model

    if not segments:
        return {"text": "", "confidence": 0.0, "no_speech_prob": 1.0,
                "duration": getattr(info, "duration", None)}

    spoken = sum(max(0.0, s.end - s.start) for s in segments)
    if spoken > 0:
        mean_logprob = sum(s.avg_logprob * max(0.0, s.end - s.start) for s in segments) / spoken
    else:
        mean_logprob = sum(s.avg_logprob for s in segments) / len(segments)

    return {
        "text": "".join(s.text for s in segments).strip(),
        "confidence": round(math.exp(mean_logprob), 4),
        "no_speech_prob": round(max(s.no_speech_prob for s in segments), 4),
        "duration": getattr(info, "duration", None),
    }
