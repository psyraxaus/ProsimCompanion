# Example speech recognition server (faster-whisper)

A small server that lets ProsimCompanion use [faster-whisper](https://github.com/SYSTRAN/faster-whisper)
for speech recognition. In the app it is the server type **Simple (/transcribe)**. It does
not ship in the installer: you run it on a PC on your network.

The user guide is [docs/manual/16-speech-recognition-server.md](../../docs/manual/16-speech-recognition-server.md).
The exact request and reply are in [docs/integrations/speech.md](../../docs/integrations/speech.md).

**Status (2026-11-16): written for this project and not yet run.** The dev PC has no Python.
Run it once on your server PC before you rely on it.

## Run it

You need Python 3.10 or later. For an NVIDIA graphics card you also need the libraries that
faster-whisper names on its own page (link above). Without a graphics card it runs on the
processor, slower.

```
pip install -r requirements.txt
uvicorn server:app --host 0.0.0.0 --port 8000
```

The first start downloads the model. `--host 0.0.0.0` makes the server answer other PCs on
your network, not only its own.

## Settings

Environment variables, all optional:

| Variable | Meaning | Default |
|---|---|---|
| `ASR_MODEL` | faster-whisper model name or folder | `large-v3` |
| `ASR_DEVICE` | `auto`, `cuda` or `cpu` | `auto` |
| `ASR_COMPUTE` | compute type, for example `float16` or `int8` | `default` |
| `ASR_LANGUAGE` | language code; empty lets the model detect it | `en` |

## Check it

```
curl http://localhost:8000/health
curl -F "file=@test.wav" http://localhost:8000/transcribe
```

The first answers `{"status":"ok","model":"large-v3"}`. The second answers JSON with
`"text"`, `"confidence"` and `"no_speech_prob"`.
