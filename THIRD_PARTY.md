# Third-party vendored assets

Assets copied into this repository verbatim (as opposed to NuGet package references, which are
listed in `Directory.Packages.props`). Each entry records exact provenance so the file can be
re-verified against upstream.

## Silero VAD model — `src/ProsimCompanion.Speech/Recognition/Vad/silero_vad.onnx`

- Upstream: https://github.com/snakers4/silero-vad
- Version: **v6.2.1** (tag `v6.2.1`, commit `7e30209a3e901f9842f81b225f3e93d8199902b1`)
- Upstream path: `src/silero_vad/data/silero_vad.onnx`
- Git blob SHA (verify with `git hash-object`): `80c5592ef1f4c9ede3e357bbd02eb863358a6a9d`
- Size: 2,327,524 bytes
- Licence: **MIT** (Copyright (c) 2020-present Silero Team) —
  https://github.com/snakers4/silero-vad/blob/v6.2.1/LICENSE
- Used by `SileroVadClassifier` (speech recognition voice-activity gating). Shipped as a
  content file beside the app; never downloaded at runtime. The ONNX I/O contract mirrors the
  upstream C# example (`examples/csharp/SileroVadOnnxModel.cs` at the same tag): inputs
  `input` [1, context+512], `sr` [1], `state` [2, 1, 128]; outputs `output`, `stateN`;
  64-sample context carry-over at 16 kHz.
