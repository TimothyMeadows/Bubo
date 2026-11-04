# VFX package contracts and fixed-input preparation

`vfx-assets` preserves the data-only `bubo-vfx-package/v1` contract and adds explicit atlas package/v2 plus bounded fixed-input preparation. The `validate` and `inspect` operations validate the complete declaration and inspect the declared local dependency bytes. They do not create lifecycle state or write output files. Existing MODEL_TO_ACTION v1 recipes, cue kinds, exported packages and acceptance semantics remain unchanged.

```text
bubo vfx-assets validate --api-version 2 --root PROJECT_ROOT --host-context CONTEXT_JSON --package INPUT_PACKAGE_JSON
bubo vfx-assets inspect --api-version 2 --root PROJECT_ROOT --host-context CONTEXT_JSON --package INPUT_PACKAGE_JSON
```

Use an explicitly built Bubo CLI. The package must be registered with kind `input` and its exact SHA-256 in the supplied `bubo-host-context/v1`; merely placing it beneath a declared input root is insufficient. Its style hash and all four engine identity fields must exactly match the full art/engine host context. A Blender-inspection-only or media-only context cannot supply this authority. The host context and references are checked for freshness before and after inspection. Bubo validates supplied selections and referenced bytes without interpreting host instruction documents or selecting an engine.

Success returns an API 2 `bubo-execution-result/v1` envelope with `status: "succeeded"`. Semantic failures return `status: "failed"`, a nonzero exit code, and `data.validation.issues` containing `code`, `path` and `message`. Malformed input, missing/changed/unsafe dependencies, host binding failures and unavailable operations fail with a nonzero exit code. The structured result remains on stdout; diagnostic logs use stderr. A semantic invalid declaration is reported before opening its declared resource paths.

## Package shape

JSON property names and string discriminators are case-sensitive. Every field below is required, including zero capacities and empty resource-reference arrays where permitted. Unknown, duplicate, missing, null and wrong-case properties are rejected at every object level. Numeric strings, fractional counts, integer overflow, null array elements and executable recipe fields are rejected. There are no callbacks, shader strings, tool selections, network inputs or import instructions in this schema.

```json
{
  "schemaVersion": "bubo-vfx-package/v1",
  "id": "owned-effect",
  "version": "v1",
  "styleSha256": "<64 lowercase hexadecimal characters>",
  "engine": {
    "id": "THREEJS",
    "contractSha256": "<exact host contract hash>",
    "definitionSha256": "<exact host definition hash>",
    "registrySha256": "<exact host registry hash>"
  },
  "budgets": {
    "maxPackageBytes": 65536,
    "maxDependencyBytes": 4096,
    "maxTotalDependencyBytes": 8192,
    "maxDependencies": 2,
    "maxEffects": 1,
    "maxLayers": 2,
    "maxParticles": 32,
    "maxVertices": 128,
    "maxDrawCalls": 4,
    "maxTrailPoints": 16,
    "maxDurationSeconds": 4
  },
  "resources": [
    {
      "id": "spark",
      "kind": "texture-png",
      "path": "textures/spark.png",
      "sha256": "<exact file hash>",
      "bytes": 128
    }
  ],
  "effects": [
    {
      "id": "cast",
      "durationSeconds": 2,
      "layers": [
        {
          "id": "charge",
          "kind": "sprite",
          "startSeconds": 0,
          "durationSeconds": 1,
          "resourceIds": ["spark"],
          "maxParticles": 16,
          "maxVertices": 32,
          "maxDrawCalls": 1,
          "maxTrailPoints": 0
        }
      ]
    }
  ]
}
```

The placeholders above are explanatory and do not constitute a valid package. Hashes and byte counts must come from the actual selected local files. Identifiers use 1–80 ASCII letters, digits, periods, underscores or hyphens and start with a letter or digit. Resource IDs, effect IDs, per-effect layer IDs and resource paths cannot collide through letter casing. References resolve exact resource IDs, with no case folding. Every declared resource must be referenced, and every reference must be declared; unrelated filesystem siblings are not imported.

## Resources and layer bounds

Resources admit `texture-png` with a `.png` extension or `mesh-glb` with `.glb`. A resource path is relative to the package directory and uses canonical forward slashes. Traversal, absolute dependency paths, Git metadata, device names, alternate streams, ambiguous separators and trailing dot/space aliases are rejected. Package arguments can use a safe absolute project-local path; reports use project-relative identities. Symlinks and reparse points are rejected in package/dependency path components. The package cannot alias a dependency, and dependencies cannot alias one another through their checked path identities.

Inspection streams dependencies under finite byte limits, verifies actual byte count and SHA-256, and checks a bounded file envelope. PNG checks cover the signature, IHDR/chunk structure, CRC, order and termination, with at most 65,536 chunks. GLB checks cover the v2 container/chunk structure, a JSON declaration bounded to 1 MiB and depth 32, one internal buffer with an optional BIN chunk, and buffer/view bounds. GLB `uri` properties, including data URIs, and extension declarations are rejected rather than leaving untracked dependencies outside the declared inventory. V1 keeps these envelope-only checks. Explicit v2 atlas textures additionally undergo bounded lossless PNG decoding on the same open file, exact dimension/color-profile checks and a second digest comparison. GLB remains envelope-only; neither profile loads model geometry or certifies a renderable asset.

The declared layer kinds are `sprite`, `ring`, `mesh`, `burst`, `projectile`, `trail`, `impact`, `ground-indicator` and `beam`. Declaring a kind does not provide its renderer. A `mesh` layer requires exactly one GLB and may also reference PNGs. `projectile` and `impact` require visual resources and admit at most one GLB. Other kinds accept PNG resources only; `sprite` requires at least one. Procedural kinds can have an empty reference array when the overall package still has a nonempty, fully referenced inventory.

Each effect has a positive finite duration. Each layer has a nonnegative finite start and positive finite duration wholly inside that effect. Vertex and draw-call limits are positive; particle and trail-point limits can be zero. Capacities and layer counts are summed conservatively across every layer, including layers whose intervals do not overlap. Effect durations are also summed against the duration budget. These are declaration bounds; actual overdraw, memory use, frame cost, accessibility and visual readability require later target evidence.

All budgets are explicit and positive. A package may narrow these absolute ceilings:

| Boundary | Absolute ceiling |
| --- | ---: |
| Package JSON | 1 MiB; depth 32 |
| One dependency | 1 GiB |
| All dependencies | 4 GiB |
| Dependencies / effects / layers | 1,024 / 64 / 1,024 |
| Particles / vertices | 1,048,576 / 16,777,216 |
| Draw calls / trail points | 65,536 / 1,048,576 |
| Aggregate effect duration | 3,600 seconds |

Inspection checks cancellation before admission, during streamed work and before return. It rechecks package identity after reading dependencies. The existing host-context reference hashing runs separately from these VFX byte/cancellation bounds. Filesystem/path guards and host freshness checks are cooperative snapshots; they are not an operating-system sandbox, physical hardlink identity guarantee or a transaction against hostile concurrent mutation. The result binds the bytes observed in that inspection and grants no durable acceptance or approval. Consumers must retain immutable versions and revalidate changed inputs.

## Available evidence and limits

The result includes the exact package path/hash/size, observed resource identities, semantic issues, declared layer/resource requirements, and capability flags. Contract validation and dependency/envelope inspection are available. Full resource-content validation, atlas/sheet/sequence preparation, video decoding, media generation, renderer execution, action composition, promotion and runtime acceptance remain unavailable. No native worker, decoder, image provider, engine adapter, renderer or tool installation is invoked. A new engine, resource processor or action contract requires its own delivery and evidence.

The executable suite uses a newly generated 1×1 RGBA PNG and a minimal owned GLB container. It covers strict JSON admission, semantic/resource closure and finite budgets; missing, changed, wrong-size, malformed, escaping, case-colliding and linked dependencies; exact host input/style/engine bindings; cancellation; and absence of filesystem writes. The optional CLI path also exercises actual public success and failure envelopes. Fixtures and their synthetic host policy data establish no production license acceptance, visual quality, human review, gameplay authority or browser/MAUI/device support.

```powershell
dotnet run --project src/runtime/vfx-assets/Tests/Vfx.Tests.csproj --configuration Release
dotnet run --project src/runtime/vfx-assets/Tests/Vfx.Tests.csproj --configuration Release -- --cli src/Bubo.Cli/bin/Release/net10.0/bubo.dll
```

These development commands use the existing .NET 10 SDK and BCL only; no external test packages are required. The CLI argument selects an existing built artifact. A missing native/browser/device check remains not run and cannot receive a verified support claim.


## Fixed-input preparation

```text
bubo vfx-assets prepare --api-version 2 --root PROJECT_ROOT --host-context CONTEXT_JSON --request inputs/effect/preparation.json --output .art-staging/tasks/TASK/vfx/ASSET/VERSION
bubo vfx-assets verify-preparation --api-version 2 --root PROJECT_ROOT --host-context CONTEXT_JSON --manifest .art-staging/tasks/TASK/vfx/ASSET/VERSION/preparation.json
```

Preparation accepts one strict `bubo-vfx-preparation/v1` request with `id`, `version`, exact `styleSha256` and `engine`, one `profile`, ordered `sources`, `timing`, `atlas` and finite `budgets`. Every source has a canonical project-relative `path`, exact lowercase `sha256` and positive `bytes`. Register the request and every source as exact host `input` references. Original rights and independent/human review remain host responsibilities; execution alone grants none.

The three profiles are:

- `sequence`: an exact ordered PNG source list. `timing.firstFrame`, `frameCount` and `frameStep` select the span; no filename sorting, wildcard discovery or missing-frame substitution occurs. Selected images must have identical dimensions.
- `sheet`: exactly one PNG and a `sheet` object declaring `columns`, `rows`, `cellWidth`, `cellHeight`, `originX`, `originY`, `strideX`, `strideY` and ordered `frameIndices`. The selected cells must fit the actual decoded source.
- `math-mask`: no source files, one frame, and a typed `mask` declaring `kind` (`disc`, `ring` or `ramp`), `width`, `height`, normalized `innerRadius`, `outerRadius`, `softness` and `angleDegrees`. These fixed formulas are compiled code; there are no expression, script or shader strings.

`timing` also declares positive rational `rateNumerator`/`rateDenominator` and `loop: once|repeat`. The atlas records reduced rational duration, selected source-frame identities and explicit frame order. Repetition is playback intent; identical endpoints or valid pixels do not establish a seamless or artistically acceptable loop.

`atlas` declares `columns`, transparent `padding`, edge `extrusion`, `alpha: straight` and `colorSpace: srgb|linear`. Coordinates use top-left PNG pixels. Each ordered frame has a complete cell rectangle and a separate interior rectangle; edge extrusion sits outside the interior and transparent padding surrounds the extrusion. Unused cells remain transparent. No sample conversion, resizing, premultiplication or gamma correction occurs. Unlabeled supported PNG samples take the explicitly selected interpretation; conflicting sRGB/gAMA declarations and unsupported ancillary profiles are rejected. Inputs support noninterlaced 8-bit RGBA or gray-alpha only. Normalize other formats explicitly outside this operation.

## Bounds and immutable attempts

Required budgets are `maxSourceBytes`, `maxTotalSourceBytes`, `maxChunkBytes`, `maxCompressedBytes`, `maxFrames`, `maxDecodedPixels` (per source), `maxAtlasPixels`, `maxPixelWork`, `maxWorkingBytes`, `maxOutputBytes`, `maxOutputFiles`, `maxSeconds` and `minimumFreeDiskBytes`. Absolute ceilings additionally limit sources/frames to256, dimensions to8192, image pixels to16,777,216, source bytes to128MiB per file/512MiB total, working allocations to512MiB and total output to1GiB. Caller budgets only narrow these limits.

The pixel port reserves two atlas buffers, one decoded source, row/chunk buffers and bounded frame metadata. The filesystem coordinator reserves an additional16MiB for finite request/receipt bookkeeping. These are explicit live-allocation estimates, not managed-heap, zlib, process RSS or OS memory guarantees. `hardProcessMemoryBytes`, or a host `maxPrivateBytes`, is rejected before writing because no hard-memory worker is admitted. Pixel work counts declared decode, copy, extrusion and atlas passes; byte/chunk/time bounds separately cover encoded processing. Output writing and reopening use atlas-specific bounds rather than incorrectly reusing a smaller source allowance.

A permanent task/asset/version claim under the supplied state root wins with exclusive creation before the output attempt starts. The output directory must be absent beneath the declared non-runtime staging root. Exact source snapshots, request, start receipt, atlas and package are retained. File budgets count source snapshots plus five portable files and the one state claim; byte budgets include the state claim. No operation overwrites, resets or deletes an existing attempt. Failure, cancellation and process interruption before the commit point retain the claim and partial files without a successful seal. The exclusive, flushed final seal is the commit point; a late cancellation does not undo a committed result. A process interrupted at that boundary has an uncertain outcome: run read-only verification on the existing seal, retaining all bytes, before deciding whether it completed. For an unsealed failed attempt, continue with an explicitly chosen new version and output directory; no old decision or generation allowance is inherited.

The final `preparation.json` seals the exact portable file inventory after independent output reopening, pixel comparison, typed package validation and repeated input/closure freshness checks. `verify-preparation` reopens that inventory, checks the exact native build/policy/task/version claim, reproduces the pixels from immutable snapshots, compares actual output pixels and complete package metadata, then rechecks current original inputs. Unexpected files/directories, missing files, changed sources, output mutations and transplanted identities fail closed. Guards are cooperative filesystem snapshots; they do not promise an OS sandbox or hard-link isolation.

## Portable atlas package/v2

The generated directory contains `package.json` and its single `atlas.png` dependency alongside the immutable preparation evidence. Its `bubo-vfx-package/v2` extends the existing package fields with required `atlases`: exact PNG resource identity/dimensions, top-left origin, straight alpha/color space, padding/extrusion, ordered cell/interior rectangles, rational rate/duration, loop intent and explicit effect/layer bindings. Every PNG in v2 has one atlas declaration, and bindings identify actual referenced layers. V1 rejects atlas fields and preserves its existing restrictions.

Preparation produces one bounded sprite effect as a reusable resource package, not a rendered game effect or an action controller. The engine owner must explicitly convert pixel rectangles to its UV convention once and supply actual visual evidence. Native verification, independent production review, human acceptance and promotion are separate decisions.

Video/decoder profiles, providers, generated media, arbitrary tools, rendering and promotion remain unavailable. Ordinary discovery, validation and production install nothing. Existing action-v1 compilation, gameplay authority and unrelated media workflows retain their existing boundaries.

## Measured codec cost

Five comparable1024?1024 owned-input baseline/candidate runs measured file loading, decoding and encoding separately. The stricter VFX profile adds a bounded DEFLATE framing walk and an independent Adler check: median decode increased from14.1175ms to22.6686ms (+60.57%, about8.55ms per megapixel). This exceeds the10% investigation threshold; the added validation explains the measured cost. Loading changed0.9137?0.9588ms and encoding80.2484?78.3480ms. The existing permissive proof profile avoids that additional framing walk; its separately measured codec-extraction decode overhead was6.79%. These local CPU measurements are diagnostic, not device/rendering, worst-case latency or GPU claims. Per-request time/work budgets remain mandatory.
