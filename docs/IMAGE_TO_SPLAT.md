# Image-to-splat foundation

Bubo can validate a proposed local TripoSplat request, inspect the model's Gaussian PLY representation and show a pinned setup dry-run. This delivery is **CPU-only and read-only**. It cannot install tools, load models, generate objects, render splats or promote game assets.

TripoSplat is the selected future single-image object backend. Captured-scene training with gsplat/Brush is outside this profile. Gaussian objects are a different representation from triangle meshes: an inspected file is not a rig, collision surface, physically accurate object or human-approved game asset.

## Public commands

```text
bubo image-to-splat setup --api-version 2 --root PROJECT --host-context CONTEXT_JSON
bubo image-to-splat validate --api-version 2 --root PROJECT --host-context CONTEXT_JSON --request REQUEST_JSON
bubo image-to-splat inspect --api-version 2 --root PROJECT --host-context CONTEXT_JSON --request INSPECTION_JSON
```

Use the existing `bubo-host-context/v1` with full art/engine policy and explicitly selected `LOCAL`; this names an optional local workflow, not a new primary pipeline. The host interprets STYLE and selection. Bubo checks the supplied exact style identity and current registered small request; it reads no instruction documents and changes no selector.

Register the small request as a hash-bound `input` reference. Declare its image/PLY dependencies under permitted input roots and bind their SHA-256 in the request. The new reader checks regular-file and byte bounds before streaming those data files; it does not require unbounded host-reference hashing of large data. Existing host-reference hashing remains outside this parser's envelope. No lifecycle state, locks, directories or outputs are created by these commands.

`setup` reports successful plan construction with `data.status: blocked`. The catalog contains exact source/five-weight pins and a Windows environment metadata candidate, not a usable installation. `generationAvailable` and `provisioningExecutionAvailable` remain false. `--execute`, `--load`, GPU/executable/script options, generation and promotion are rejected. No Python import, network access or GPU probe is performed.

## Request contract

`bubo-splat-request/v1` requires every field below; hashes and paths must refer to actual caller-owned/cleared local data. Placeholder values do not authorize execution.

```json
{
  "schemaVersion": "bubo-splat-request/v1",
  "backend": "triposplat",
  "styleSha256": "EXACT_LOWERCASE_64_HEX_STYLE_HASH",
  "input": { "path": "inputs/prop.png", "sha256": "EXACT_LOWERCASE_64_HEX_INPUT_HASH" },
  "gaussianCounts": [32768, 131072],
  "seed": 42,
  "budgets": {
    "maxGaussians": 131072,
    "maxInputBytes": 67108864,
    "maxDecodedBytes": 134217728,
    "maxHeaderBytes": 65536,
    "timeoutSeconds": 120,
    "maxLogBytes": 1048576,
    "maxProcessMemoryBytes": 17179869184
  }
}
```

The explicit proposed process-memory value is a declaration, not measured availability, GPU-memory enforcement or an OS sandbox. CPU request validation checks schema/invariants, style, current source identity and byte bounds. It explicitly reports that image decoding and generation are unverified. Actual preprocessing must additionally bound decoded pixels, aspect ratio, intermediate resize and nonempty foreground before any future model run.

For inspection, use `bubo-splat-inspection/v1` with only `schemaVersion`, `styleSha256`, `input` (PLY path/hash) and `budgets`. JSON is case-sensitive, bounded to 64 KiB/depth16 and rejects duplicate, missing, unknown or invalid members. Current generation density variants are 32,768 and131,072; 262,144 is not automatically enabled. The seed uses the exact safe-integer host range. Source paths are relative slash paths, never URLs, parent traversal or arbitrary executables.

## Explicit PLY profile

`validate --request` also accepts the strict `bubo-splat-artifact/v1` master descriptor. It requires backend/style, original `input` and Gaussian `payload` path/hash identities, profile/count, explicit budgets, and `coordinates: source-local`, `units: uncalibrated`, `scaleConfidence: unverified`. Its ancestry requires request/environment/runtime-build/resource-catalog SHA-256 identities, exact source/model Git revisions, and an explicitly present nullable parent artifact SHA-256. Original image and payload paths must be distinct.

Artifact validation binds the descriptor bytes actually parsed to the registered reference, checks current bounded source/payload hashes, streams the PLY and compares actual count/profile. Ancestry declarations are structurally checked; their referenced builds/installations/models and construction history are not verified by this foundation. Results report `ancestryVerified: false` and no runtime acceptance. These declarations grant no approval and do not certify a real generation. A later producer/receiving boundary must establish the actual immutable construction chain before promotion.

`triposplat-ply-sh0/v1` admits binary little-endian PLY1.0 with one bounded vertex element and exactly17 float32 properties: `x/y/z`, `nx/ny/nz`, `f_dc_0..2`, `opacity`, `scale_0..2` and `rot_0..3`. Property order may vary. Higher SH coefficients, lists, faces, unknown/duplicate properties, other encodings and trailing payloads fail explicitly.

Positions are source-local and uncalibrated; measured units and target coordinate mapping need separate evidence. Scale is natural-log standard deviation, opacity is a logit, rotation is a nonzero bounded WXYZ quaternion normalized by the receiver, and colour is RGB SH0 coefficients. The inspector reports raw position bounds, count, source hash and encoded scalar bytes. Those bytes are not a GPU allocation estimate. It streams one68-byte record at a time after validating count-times-stride, file/header/decoded limits; cancellation, nonfinite values, extreme numeric values, truncation and changed identities fail closed.

## Licensing and remaining work

Original Bubo code remains MIT. The catalog distinguishes TripoSplat's own MIT terms, DINOv3's custom agreement, FLUX.2 autoencoder Apache-2.0, BiRefNet MIT and environment-specific notices. Apache-2.0 commercial components are permitted when their obligations are met; an MIT headline does not replace other component terms.

The inspected upstream implementation embeds component architectures without a complete ancestry/notice map. Converted-weight provenance, applicable agreements and actual wheel/native payload notices/security review remain incomplete. The metadata-only Windows candidate is a new isolated Python3.12.16/Torch2.7.1+cu128/TorchVision0.22.1+cu128 environment, not a modification of existing image/audio environments. Model bytes, native imports, mixed precision/deformable convolution, RTX/VRAM fit and output quality have not been verified for this profile.

No setup/generation permission follows from this catalog. Later delivery needs an authorized complete installation, model/licence records, source rights, bounded worker and existing shared GPU/attempt ownership, immutable staging, independent reload/review and separate human output acceptance. Rendering/Quarks/postprocessing and actual MAUI device evidence remain host responsibilities.
