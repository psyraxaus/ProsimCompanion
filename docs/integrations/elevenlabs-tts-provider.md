# ElevenLabs TTS provider — investigation & design (2026-09-27)

Requested by Peter: add ElevenLabs as a voice option. This note covers the API as it stands
today, how it maps onto the existing `ITtsProvider` chain, and the implementation plan. Verify
tier/pricing details against https://elevenlabs.io/pricing before shipping docs — they move.

## 1. API summary

| Item | Value |
|---|---|
| Synthesis | `POST https://api.elevenlabs.io/v1/text-to-speech/{voice_id}` |
| Streaming variant | `POST …/v1/text-to-speech/{voice_id}/stream` (chunked bytes; not needed for v1) |
| Auth | header `xi-api-key: <key>` |
| Query | `output_format` (default `mp3_44100_128`), `optimize_streaming_latency` 0–4, `enable_logging` |
| Body | `text` (req), `model_id` (default `eleven_multilingual_v2`), `voice_settings {stability, similarity_boost, style, use_speaker_boost, speed}`, `language_code`, `seed`, `previous_text`, `next_text`, `apply_text_normalization` (`auto|on|off`), `pronunciation_dictionary_locators` (≤3) |
| Response | raw audio bytes in `output_format`; 422 on validation error, 401 bad key, 429 quota/concurrency |
| Voices | `GET https://api.elevenlabs.io/v2/voices?category=premade` → `voices[] { voice_id, name, category, labels{accent,gender,…}, preview_url, settings }` |

### Models (real-time relevant)

| model_id | Latency | Chars/req | Notes |
|---|---|---|---|
| `eleven_flash_v2_5` | ~75 ms | 40 000 | Half the credit cost of the standard models; 32 languages. **Default.** |
| `eleven_v3` (conversational mode) | ~280 ms | — | Most expressive, supports audio tags. Optional "premium" setting. |
| `eleven_multilingual_v2` | not real-time | 10 000 | Long-form quality; skip. |
| `eleven_v3` (standard) | not real-time | 5 000 | Skip. |

### Output formats and tiers — the important constraint

`TtsAudio` requires a complete RIFF/WAV. ElevenLabs does not emit WAV. Options:

- **MP3** (`mp3_44100_128`, `mp3_22050_32`) — available on every tier including Free. Needs a
  decode step → NAudio `MediaFoundationReader` (Windows-only; acceptable, the app is Windows).
- **PCM S16LE** (`pcm_16000|22050|24000|44100`) — paid-tier feature. The official pricing page
  lists "44.1 kHz PCM audio output via API" at **Pro ($99/mo)**; community docs report the lower
  PCM rates unlock at Creator. Either way: **not available on Free/Starter**. Wrap with a 44-byte
  WAV header ourselves (cheap, no decode).

**Decision:** default `mp3_44100_128` + decode. Expose `ElevenLabsOutputFormat` so paid-tier users
can pick `pcm_24000` (skips the decode; use 24 kHz to match Google's LINEAR16 rate).

### Plans (Sept 2026, from elevenlabs.io/pricing)

| Plan | $/mo | Credits/mo | Notes |
|---|---|---|---|
| Free | 0 | 10 000 | **No commercial licence.** Cap ~2 500 chars per generation. |
| Starter | 6 | 30 000 | Commercial licence from here. |
| Creator | 11 | 121 000 | |
| Pro | 99 | 600 000 | 44.1 kHz PCM, 192 kbps MP3. |
| Scale / Business | 299 / 990 | 1.8 M / 6 M | |

1 credit ≈ 1 character on standard models; Flash v2.5 is billed at roughly half. Concurrency limits
scale with tier and are not fully documented — the arbiter already serialises speech, so a single
in-flight request is the norm.

Licensing note for user docs: the end user supplies their own key, so the commercial-use question
is theirs, not ours. JC's own testing on Free is fine.

## 2. Fit against the existing chain

Reference implementations: `KokoroTtsProvider` (shape) and `GoogleTtsProvider` (paid-provider
patterns: budget check before the call, uncapped cache, latch-on-failure, options-change reset).

Chain order becomes **Kokoro → ElevenLabs → Google → WinRT → SAPI5**. Registration order in
`SpeechServiceCollectionExtensions.AddSpeechServices` *is* the chain, so it's one added line.
Rationale for ElevenLabs ahead of Google: a user configures at most one paid cloud provider;
whichever is configured wins. If someone genuinely wants both, add a `PreferredCloudTts` enum
later — not now.

## 3. Implementation plan

### 3.1 `SpeechOptions` — new fields (Core/Configuration/SpeechOptions.cs)

```csharp
// ---- ElevenLabs (paid cloud neural TTS) ----

/// <summary>API key (xi-api-key); empty disables the provider. Never logged.</summary>
public string ElevenLabsApiKey { get; set; } = "";

/// <summary>Base URL; overridable for proxies/regional endpoints.</summary>
public string ElevenLabsBaseUrl { get; set; } = "https://api.elevenlabs.io";

/// <summary>ElevenLabs voice_id (from GET /v2/voices). No default — the user picks.</summary>
public string ElevenLabsVoiceId { get; set; } = "";

/// <summary>Model: eleven_flash_v2_5 (fast, half cost) or eleven_v3 (expressive).</summary>
public string ElevenLabsModelId { get; set; } = "eleven_flash_v2_5";

/// <summary>Output format. mp3_44100_128 works on every tier and is decoded locally;
/// pcm_24000 needs a paid tier but skips the decode.</summary>
public string ElevenLabsOutputFormat { get; set; } = "mp3_44100_128";

/// <summary>Voice settings 0..1. Stability high-ish for a consistent FO.</summary>
public double ElevenLabsStability { get; set; } = 0.6;
public double ElevenLabsSimilarityBoost { get; set; } = 0.75;
public double ElevenLabsSpeed { get; set; } = 1.0;

/// <summary>Cloud hop — looser than Kokoro's 1500 ms.</summary>
public int ElevenLabsTimeoutMs { get; set; } = 8000;

/// <summary>Monthly character budget; plan-relative (Free 10k, Starter 30k, Creator 121k).
/// 0 disables enforcement.</summary>
public int ElevenLabsMonthlyCharBudget { get; set; } = 9_000;

/// <summary>Send apply_text_normalization=off — the arbiter already emits spoken-form
/// phraseology (FL350 → "flight level three five zero") and ElevenLabs' auto normaliser
/// mangles aviation tokens.</summary>
public bool ElevenLabsTextNormalization { get; set; } = false;
```

### 3.2 `TtsUsageTracker` — namespace per provider

Currently a single `usage.json` counter whose log line says "Google TTS usage". Add an optional
`provider` parameter (default `"google"`) to `CharactersThisMonth`, `IsOverBudget`, `Increment`;
file becomes `usage.json` for google (migration-compatible) and `usage.elevenlabs.json` otherwise;
log line uses the provider name. `GoogleTtsProvider` calls are unchanged.

### 3.3 `ElevenLabsTtsProvider` (Speech/Tts/ElevenLabsTtsProvider.cs)

Mirror `KokoroTtsProvider`; differences:

- `Name => "elevenlabs"`, `IsNetworkProvider => true`,
  `IsConfigured => ApiKey && VoiceId non-empty`.
- Cache key voice segment = `$"{voice}@{modelId}@{outputFormat}"` — `TtsDiskCache` keys on
  provider+voice+text only, so the model/format must ride in the voice segment or a model switch
  serves stale audio. Cache uncapped (`maxMbPerVoice: 0`) like Google — paid audio.
- Budget check via `_usage.IsOverBudget(root, budget, text.Length, "elevenlabs")` **before** the
  call; throw so the router falls through.
- Request:
  ```csharp
  var url = $"{baseUrl}/v1/text-to-speech/{Uri.EscapeDataString(voice)}?output_format={fmt}";
  using var req = new HttpRequestMessage(HttpMethod.Post, url);
  req.Headers.Add("xi-api-key", options.ElevenLabsApiKey);
  req.Content = JsonContent.Create(new {
      text,
      model_id = options.ElevenLabsModelId,
      voice_settings = new {
          stability = options.ElevenLabsStability,
          similarity_boost = options.ElevenLabsSimilarityBoost,
          speed = options.ElevenLabsSpeed },
      apply_text_normalization = options.ElevenLabsTextNormalization ? "auto" : "off",
  });
  ```
- Response → WAV:
  - `pcm_*` → `WavHeader.Wrap(bytes, sampleRate, channels: 1, bits: 16)` (new tiny helper next to
    `WavRepair`, or extend `WavRepair`).
  - `mp3_*` → `new MediaFoundationReader(tempFileOrStream)` → `WaveFileWriter` to a
    `MemoryStream`. Note `MediaFoundationReader` wants a seekable stream; buffer first.
- Error mapping: 401 → latch "bad key" until options change (Google's `_clientFailed` pattern);
  429 → throw with the body so the router cools it down; 422 → throw (bad voice/model id).
- Redact: never log the key; log HTTP status + first 160 chars of body only.
- `_usage.Increment(root, text.Length, "elevenlabs")` only on a real call, never cache hits.

### 3.4 Registration

`SpeechServiceCollectionExtensions.AddSpeechServices`: insert
`services.AddSingleton<ITtsProvider, ElevenLabsTtsProvider>();` between Kokoro and Google.

### 3.5 `TtsPrewarmService.PickCachingProvider()`

Currently kokoro-else-google. Change to: kokoro → (if !localOnly) elevenlabs → google, and the
`foVoice` selection needs an `elevenlabs` branch (`ElevenLabsVoiceId`). Pre-warming the checklist
phrase set is the single biggest cost lever — it converts the per-flight spend to a one-off.

### 3.6 Role voices

`voiceOverride` ids are "in the provider's own vocabulary". Wherever role voice ids are configured
(cabin/role `VoicesOptions`), they'll need an ElevenLabs voice_id per role, same as the existing
Kokoro/Google split. Check how that options class distinguishes provider today before adding.

### 3.7 `SpeechSettings.razor`

New `_section == "elevenlabs"` panel, modelled on the Google section:

- API key (masked `TextField`; hint "never logged")
- Voice id + a **"Fetch voices"** button → `GET /v2/voices?category=premade`, populate a select
  with `name (accent, gender)`. Nice-to-have: play `preview_url`.
- Model select: `eleven_flash_v2_5` / `eleven_v3`
- Output format select: `mp3_44100_128` (all tiers) / `pcm_24000` (paid tiers)
- Stability / similarity / speed sliders
- Timeout, monthly character budget (hint: plan-relative figures), text normalisation toggle
- Usage this month read-out (from tracker)

### 3.8 Docs

- `docs/integrations/speech.md`: add ElevenLabs to the router line and a bullet with the
  endpoint/format/tier notes above.
- User doc: which plan is needed, that MP3 is the safe default, commercial-licence note.

### 3.9 Tests

- `TtsRouterTests`: chain order includes elevenlabs; unconfigured → skipped without failure.
- `ElevenLabsTtsProviderTests` with a stubbed `HttpMessageHandler`: header set, body shape,
  budget-exceeded throws before any HTTP, 401 latches, pcm wrap produces valid RIFF, mp3 path
  decodes a known small fixture.
- `TtsUsageTracker` per-provider file separation.

## 4. Later / out of scope for v1

- **Streaming** (`/stream` endpoint, or WebSocket) once sentence-chunked LLM streaming lands —
  `previous_text`/`next_text` keep prosody continuous across chunks.
- `pronunciation_dictionary_locators` for stubborn airline/waypoint names.
- `seed` for deterministic re-generation of cached phrases.
- Key storage hardening (DPAPI) if the config file is ever shared in diagnostics bundles — check
  the `Export diagnostics` reducer redacts `ElevenLabsApiKey`.

## 5. Sources

- https://elevenlabs.io/docs/api-reference/text-to-speech/convert
- https://elevenlabs.io/docs/overview/capabilities/text-to-speech
- https://elevenlabs.io/docs/api-reference/voices/search
- https://elevenlabs.io/pricing
- https://elevenlabs.io/blog/pcm-output-format
- https://github.com/dnvriend/elevenlabs-tts-tool/blob/main/references/free-tier.md (community; tier gating for PCM)
- https://github.com/tryAGI/ElevenLabs, https://www.nuget.org/packages/ElevenLabs-DotNet (community SDKs — not used)
