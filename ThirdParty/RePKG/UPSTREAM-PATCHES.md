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

Regression coverage: `TexBudgetRegressionTests` exercises structural failures,
malicious-fixture timing, cumulative overflow, and every public numeric limit at
`limit-1`, `limit`, and `limit+1`.

## ImageSharp ownership and bounded encoding

- `TexToImageConverter` explicitly disposes source images, GIF canvases,
  sequence images, frame clones, and streams on success and failure paths.
- PNG/GIF conversion validates a conservative encoded upper bound before image
  allocation and writes through a bounded stream so output cannot exceed the
  file budget.
- `RePkgTextureConverter.cs` uses the same file scope for parsing and encoding,
  and the application service shares one batch budget across an unpack request.

Regression coverage: `TexOwnershipRegressionTests` checks raw and GIF loops,
ImageSharp undisposed-allocation diagnostics, process memory/handle bounds, and
encoding-failure cleanup.

## Bounded C strings

- `Extensions.ReadNString` reads strict UTF-8 bytes, requires a NUL terminator,
  enforces a 64 KiB default content limit, and leaves the stream immediately
  after the terminator. Version 4 TEX condition strings use this bounded path.

Regression coverage: `TexStringAndPixelRegressionTests` covers NUL at empty,
`limit-1`, and `limit`, rejection at `limit+1`, missing NUL, invalid UTF-8,
stream position, and an oversized version 4 condition.

## RG88 pixel semantics

- `Texture/Helpers/RG88.cs` fixes boxed equality to compare `RG88` values and
  converts pixels as grayscale `G,G,G` with alpha `R`, consistent with the
  type's established vector/color semantics.

Regression coverage: `TexStringAndPixelRegressionTests` checks equality/hash
and converts a minimal real RG88 TEX through the product adapter to PNG.

## Deliberate non-changes

- ImageSharp remains on the repository's 2.1.x line; a major-version migration
  is outside the v1.2.1 patch scope.
- Unrelated RePKG naming, style, analyzer, and legacy API issues are unchanged.
