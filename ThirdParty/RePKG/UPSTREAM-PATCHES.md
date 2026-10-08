# RePKG local patches

Wallpaper Field vendors RePKG 0.4.0 source under this directory. The files
below intentionally differ from that upstream baseline. These are narrow
compatibility and security patches for untrusted Wallpaper Engine TEX input;
they are not represented as unmodified upstream code.

## TEX resource and validation budget

- Added `Source/RePKG.Application/Texture/TexDecodeBudget.cs` as the single
  owner of dimension, pixel, count, compressed/decoded byte, encoded-output,
  per-file, and batch limits. All cumulative arithmetic uses checked `long`.
- Threaded one file scope through `TexReader`, `TexHeaderReader`,
  `TexImageContainerReader`, `TexImageReader`, `TexFrameInfoContainerReader`,
  `TexMipmapDecompressor`, and `TexToImageConverter` while retaining
  compatibility constructors for existing callers.
- Validate counts, dimensions, payload lengths, GIF frame coordinates/crops,
  image identifiers, and output capacity before attacker-sized allocation or
  iteration. LZ4 output must exactly match its declared length, and malformed
  decoder failures are reported as controlled `UnsafeTexException` failures.
- `Texture/Helpers/DXT.cs` now requires the exact block payload size before
  allocating its RGBA result.
- Conversion staging/native pixel storage also debits the same file and batch
  decoded-byte limits before image allocation. PNG reserves eight bytes per
  cropped output pixel; GIF reserves ten bytes for every expanded frame pixel,
  including repeated references to the same source image.
- Rotated GIF frame dimensions must match the canvas during parsing, before
  conversion can allocate a large canvas for an incompatible tiny frame.

Regression coverage: `TexBudgetRegressionTests` exercises structural failures,
malicious-fixture timing, cumulative overflow, and every public numeric limit at
`limit-1`, `limit`, and `limit+1`.

## Windows image encoding and bounded output

- `TexToImageConverter` uses Windows WPF/WIC PNG and GIF encoders. The
  ImageSharp dependency and custom ImageSharp pixel interfaces were removed.
  `RePKG.Application` targets `net10.0-windows` with WPF enabled;
  `RePKG.Core` retains its `netstandard2.0` target.
- `TexPixelConverter` copies only the required crop, translates RGBA/RG88/R8
  into BGRA, and performs right-angle rotations with exact pixel indexing.
  PNG crops remain centered, matching the earlier converter.
- Frozen WPF bitmap sources remain scoped to each encoding operation. GIFs
  are encoded one frame at a time, so the encoder does not retain every
  expanded frame. Native WIC resources follow WPF's managed lifetime; they
  are not treated as `IDisposable`. Streams are disposed and encoder frame
  references are cleared on success and failure.
- GIF frames with up to 256 colors use exact palettes. Larger palettes use
  WIC quantization; transparency reserves palette index zero and uses the
  GIF binary alpha threshold of 128. The GIF palette limit is 256 entries.
- WPF's GIF encoder does not support frame metadata. `GifFrameAssembler`
  validates each single-frame WIC output, copies its palette and compressed
  pixels, and writes explicit frame delays using the original float midpoint
  rounding. Unspecified disposal and play-once behavior match the prior
  converter defaults. It rejects malformed
  blocks, missing palettes, mismatched dimensions and extra images.
- PNG/GIF conversion validates a conservative encoded upper bound before image
  allocation and writes through a bounded stream so output cannot exceed the
  file budget.
- `RePkgTextureConverter.cs` uses the same file scope for parsing and encoding,
  and the application service shares one batch budget across an unpack request.

Regression coverage: `TexOwnershipRegressionTests` checks raw and GIF loops,
output-buffer collection, process memory/handle bounds, and later-frame
failure cleanup. `GifBudgetSecurityRegressionTests` checks decoded-budget
exhaustion before allocation, canvas validation, bounded span/async output,
decoded PNG/GIF pixels, alpha, rotation, palette quantization, frame order,
delays and disposal. `TexPixelAndGifContainerRegressionTests` additionally
exercises pixel mapping and GIF framing without Windows image APIs.

## Bounded C strings

- `Extensions.ReadNString` reads strict UTF-8 bytes, requires a NUL terminator,
  enforces a 64 KiB default content limit, and leaves the stream immediately
  after the terminator. Version 4 TEX condition strings use this bounded path.

Regression coverage: `TexStringAndPixelRegressionTests` covers NUL at empty,
`limit-1`, and `limit`, rejection at `limit+1`, missing NUL, invalid UTF-8,
stream position, and an oversized version 4 condition.

## RG88 pixel semantics

- `Texture/Helpers/RG88.cs` is a small value type with boxed equality and a
  BGRA conversion. It interprets grayscale as `G,G,G` and alpha as `R`,
  preserving the established TEX pixel semantics without a codec-specific
  pixel interface.

Regression coverage: `TexStringAndPixelRegressionTests` checks equality/hash
and converts a minimal real RG88 TEX through the product adapter to PNG.

## v1.2.2 compiled source boundary

- The complete RePKG 0.4.0 source snapshot remains vendored for review and
  license compliance. Product code calls it only through
  `WallpaperField.ThirdParty.RePKG.SafePackageReader` and
  `RePkgTextureConverter`; no service or UI module imports a RePKG namespace.
- `RePKG.Application.csproj` disables default compile discovery and uses a
  role-based set: the shared constants/extensions, all exceptions, and the TEX
  read/convert tree except `Texture/Writer`. This excludes the unused eager
  `PackageReader`/`PackageWriter` and six TEX writer/compressor sources while
  retaining every reader, safety budget, fixed fixture, and conversion path
  used by Wallpaper Field.
- `RePKG.Core` deliberately keeps its complete 42-file compile surface. Its
  shared texture object/interface model is still consumed by the retained TEX
  reader/converter, and narrowing that separate assembly was not part of the
  reviewed v1.2.2 candidate.
- `scripts/verify-repkg-compile-surface.ps1 -Mode Verify` evaluates real MSBuild
  `Compile` items, requires the Application set to equal the role-based policy,
  and reports the unchanged Core count. It avoids a manually duplicated list
  of individual TEX reader files.

Regression coverage: `UpstreamBoundaryRegressionTests` enforces the adapter
boundary and project policy. The isolated baseline/candidate experiment also
runs the complete PKG/TEX Smoke fixture, self-contained publish comparison,
assembly identity check, and license/notice hash comparison.

## Deliberate non-changes

- Already encoded image and video TEX payloads retain their passthrough
  behavior. Output-extension selection retains the upstream adapter contract.
- The upstream notice file, including its Apache 2.0 text also used for
  XamlAnimatedGif attribution, remains intact.
- Unrelated RePKG naming, style, analyzer, and legacy API issues are unchanged.
