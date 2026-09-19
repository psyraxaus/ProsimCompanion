# Third-party vendored assets

Assets copied into this repository verbatim (as opposed to NuGet package references, which are
listed in `Directory.Packages.props`). Each entry records exact provenance so the file can be
re-verified against upstream.

## JetBrains Mono — `src/ProsimCompanion.Web/wwwroot/fonts/JetBrainsMono-{Regular,Bold}.woff2`

- Upstream: https://github.com/JetBrains/JetBrainsMono
- Version: **2.304** (release asset `JetBrainsMono-2.304.zip`)
- Upstream path: `fonts/webfonts/JetBrainsMono-Regular.woff2`, `fonts/webfonts/JetBrainsMono-Bold.woff2`
- Git blob SHAs (verify with `git hash-object`): Regular `40da427651937ac3b65ea6842f9448a810f719c6`
  (92,164 bytes), Bold `4917f43410757b48e4ad1eca81b868dfe405d25b` (94,588 bytes)
- Licence: **SIL Open Font License 1.1** (Copyright 2020 The JetBrains Mono Project Authors) —
  shipped beside the fonts as `JetBrainsMono-OFL.txt`
- Used as `--font-mono` in the web UI (figures, inputs, loadsheet, split-flap) since the
  2026-09-20 Superdesign restyle (ADR-0011). Bundled so the UI never fetches a font from a
  CDN — the sim PC and the iPad may be offline.

## Public Sans — `src/ProsimCompanion.Web/wwwroot/fonts/PublicSans-{Light,Regular,SemiBold,Bold}.woff2`

- Upstream: https://github.com/uswds/public-sans
- Version: **v2.001** (release asset `public-sans-v2.001.zip`)
- Upstream path: `fonts/webfonts/PublicSans-{Light,Regular,SemiBold,Bold}.woff2`
- Git blob SHAs (verify with `git hash-object`): Light `2b92636ab5d611aa98912a2fb08b9a9beb025961`
  (33,560 bytes), Regular `581f06c35a54b6f4f282a2b3f0a29bb86f72e1ad` (33,612 bytes),
  SemiBold `7916a44476d583c0377af2fa11b1085c00760e75` (33,636 bytes),
  Bold `a216e9a2e3f6e969b899d8a8bb940b38bdc73cb0` (33,664 bytes)
- Licence: **SIL Open Font License 1.1** (Copyright 2015 The Public Sans Project Authors) —
  shipped beside the fonts as `PublicSans-OFL.txt`
- Used as `--font-sans` in the web UI (labels, body text) since the 2026-09-20 restyle.

## Lucide icons — `src/ProsimCompanion.Web/Components/AppIcons.cs`

- Upstream: https://github.com/lucide-icons/lucide (npm `lucide-static`)
- Version: **1.47.0**
- Upstream path: `icons/{layout-dashboard,file-input,file-text,clipboard-list,scale,fuel,gauge,list-checks,wrench,clock-4,settings,plane,bell,cpu,chevron-down}.svg`
- Licence: **ISC** (Copyright (c) 2026 Lucide Icons and Contributors) —
  https://github.com/lucide-icons/lucide/blob/main/LICENSE
- The inner markup of each 24×24 SVG is inlined into the `AppIcons.Shapes` dictionary and rendered
  by `AppIcon.razor` — no icon runtime is fetched from a CDN (ADR-0011: the sim PC and the iPad
  may be offline). Re-verify by diffing against the upstream file at that version.

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
