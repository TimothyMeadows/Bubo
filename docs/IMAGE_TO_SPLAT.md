# Local image-to-splat

Bubo implements bounded TripoSplat generation and independent Gaussian-package verification, alongside CPU-only request validation and streaming PLY inspection. A local Windows x64 pilot produced both supported densities from two original static-prop inputs and passed technical checks and human backend QA with documented quality limitations. The measured profile and its limits appear below. Each host still needs its own installation, resource and native-readiness checks; CPU/mock fixtures and capability flags alone establish none of those results.

This optional LOCAL workflow creates static Gaussian objects from one image. Captured-scene training, mesh reconstruction, collision, rigging, rendering and game promotion remain separate. Existing STYLE, mesh workflows and engine selection are unchanged.

## Commands

Supply `--api-version 2 --root PROJECT --host-context CONTEXT_JSON` on every command. The host provides explicit full-art LOCAL and exact STYLE/engine identities; Bubo reads no instruction documents. Register small descriptors as hash-bound inputs. Large images/PLYs use declared input roots with finite byte limits before streaming.

| Command after `bubo image-to-splat` | Behavior |
| --- | --- |
| `setup` | Read-only pinned closure/readiness metadata |
| `validate --request JSON` | CPU technical request/artifact validation; not construction ancestry authentication |
| `inspect --request JSON` | CPU streaming SH0 Gaussian facts |
| `provision --request JSON` | Installation dry-run, no writes/downloads/imports |
| `provision --request JSON --execute` | Explicit pinned acquisition and isolated environment creation; no model import/GPU |
| `setup --request JSON` | Metadata-only registration plan |
| `setup --request JSON --execute` | Explicit verification and immutable registration; no model import/GPU |
| `generate --request JSON` | One authorized candidate/revision under shared GPU ownership |
| `verify-package --request JSON` | CPU reload of actual package files, data and construction records |

Capability flags identify implemented operations. `nativeReadinessVerified:false` is separate. No plan, registration or technical check grants human model acceptance or game promotion.

## Measured native profile

The November 2026 pilot used Windows x64, an RTX 5080 Laptop GPU with 16,303 MiB reported VRAM, the pinned Python 3.12.16/Torch 2.7.1+cu128 environment, and two 1024-square RGBA PNG inputs with supplied alpha masks. Settings were seed 42, 20 steps, guidance 3, shift 3 and erosion radius 1; each sampled latent supplied both 32,768 and 131,072 Gaussians. Model placement stayed FP16 for flow/decoder/background removal and BF16 for DINO/VAE, without offload.

Each attempt declared 900 seconds, 20 GiB aggregate job committed memory, a 12 GiB Torch allocation ceiling, 2 GiB sampled residual GPU headroom, 128 MiB packaged output, 16 MiB logs and the fixed 64 MiB/4096-entry cache. Successful workers measured about 9.60 GiB peak process commitment and 5.34 GiB peak working set. Peak Torch allocation was about 4.59 GiB; ending allocated and reserved bytes were zero. Driver samples and Torch counters measure different scopes and do not establish a continuous device-wide peak.

Both cases passed independent payload/package checks and finite canonical/orbit review. Five warm CPU reload/render measurements per frozen density reproduced the same pixels; these are reference-render checks, not Spark/WebGL/MAUI or game-device benchmarks. Native inference ran once per claimed attempt, so these observations establish no five-run inference speed or regression result. The pilot used alpha masks; real RGB/BiRefNet segmentation inference, other settings and other native hosts remain unqualified by these cases.

Human backend QA accepted visible limitations: guessed hidden surfaces, altered handles, softened detail and incomplete interior appearance. The exported frame is source-local and uncalibrated; a review-page camera roll does not calibrate object up/front or physical scale. One case required separate post-run technical reconciliation after injected platform modules were reviewed; its original failed execution and spent claims remained intact. The other case completed ordinary generation and independent package verification with the module pins declared before launch. Neither result grants game acceptance, collision/animation readiness or promotion.

## Setup and licensing

Use `bubo-splat-provision/v1` with required `task`, fresh `installationRoot`, disjoint `downloadRoot`, registered `bootstrapPython:{path,sha256}`, existing `runtimeSourceRoot`/`noticeSourceRoot`, and `licenses:[{path,sha256}]`. Managed roots must be explicit project-local tool roots outside state/staging/runtime outputs. Provisioning requires explicit host permission and existing component licence evidence. Failed installs/partial files remain for explicit reconciliation; no automatic retries/overwrites.

The source pin is `d8db9e018b413dd9c4a9fe22463781bf98e8e68d`, model revision `56a96e603204ec410c4da60c13ea4fa09a2169a9`. Five weights total3,779,190,284bytes. The isolated Python3.12.16/Torch2.7.1+cu128/TorchVision0.22.1+cu128 candidate has16 wheel archives totaling3,311,402,062bytes plus a22,014,416-byte Python archive. Read the dry-run for exact hashes, acquisition and disk bounds. Setup copies exact existing app-local CRT/notices; it changes no drivers/global software and excludes CUDA13 image-runtime DLLs.

Registration uses `bubo-splat-installation-request/v1`: `task`, new `receiptPath`, `modelDirectory`, `sourceDirectory`, `environmentRoot`, `pythonBaseRoot`, `wheelhouseRoot`, `pythonArchive`, `noticeDirectory`, exact registered `python:{path,sha256}`, `licenses` and `componentReview`. It verifies the exact3-file source/5-file weight sets, bounded full inventory, archive-derived Python/wheel bytes, isolated venv and complete reviewed notices. It freshly rechecks installed bytes rather than trusting version strings or generated RECORD files.

Original Bubo code remains MIT. Retain TripoSplat MIT, the separate DINOv3 agreement, FLUX autoencoder Apache-2.0, BiRefNet MIT and framework/native terms. FLUX autoencoder and BiRefNet weights match upstream hashes exactly. The author's DINOv3 source is documented, but conversion/tensor equivalence of the repack is unverified. This profile excludes the differently licensed FLUX generator and training renderer.

The host supplies `bubo-splat-license-evidence/v1` with exact `task`, `backend:'triposplat'`, `accepted:true`, required `licenseIds` and registered rights `evidence`. A separate `bubo-splat-component-review/v1` binds current catalogue/environment specification, actual notice inventory, `commercialUseAllowed`, `noticesVerified` and the explicit `accepted-disclosed-derivative-lineage` disposition with `unresolvedComponents:['dinov3-conversion']`. Those are scoped caller records, not proof of unknown historical ancestry. Registration accepts no terms automatically.

## Generation and attempt ownership

`bubo-splat-generation/v1` requires `task`, fresh `runId`, `attemptKind:'candidate'|'revision'`, the technical `request` below, exact installation receipt and authorization identities, `settings:{steps,guidance,shift,erodeRadius}`, and `limits:{maxInputPixels,maxInputEdge,maxAspectRatio,maxOutputBytes,maxGpuAllocatedBytes,minGpuFreeBytes}`. Settings support10..20steps, guidance1..10, shift1..5, erosion0..8 and declared32768/131072 densities. Record the settings actually tested; other settings do not inherit qualification. No262144 profile, backend/device/offload fallback or automatic retry exists.

Decode only single-frame RGB/RGBA PNG/JPEG/WebP within16,777,216pixels,8192per edge and4:1aspect, including bounded resize/crop intermediates. Limits must admit the fixed1024-square prepared canvas. Transparent masks can be checked before model loading; opaque/RGB masks are checked after fixed BiRefNet segmentation, before conditioning/flow. References do not establish dimensions or unseen-side truth.

Native execution requires explicit supported committed-memory/time/log/package limits and Torch allocator/free-headroom policy. Residual device-wide free GPU memory is sampled after model loading, at flow callbacks and after each density decode, alongside the Torch allocation ceiling. These checkpoints are not continuous monitoring and cannot exclude unrelated applications. Windows job committed memory is not resident RAM or VRAM. The worker records Windows lifetime peak committed bytes separately from current private commitment and peak/current working set. Those are per-worker measurements, not aggregate job peaks. Torch measurements are not aggregate driver memory. Bubo's shared lease cannot exclude unrelated applications; coordinate GPU availability separately.

Use explicit durable host `roots.mediaState` and hash-bound `policy.media`. Splat attempts live in its separate `image-to-splat/ledger` child. A `bubo-splat-generation-approval/v1` binds `task`, exact `ledgerRoot`, source/style/catalogue hashes, `maximumCandidates:1`, `maximumRevisions:1`, `generationApproved:true`, `gpuAvailabilityConfirmed:true`, `approvedBy` and `sourceRights` identity. A separate `bubo-splat-source-rights/v1` binds source hash, `scope:'local-generation'`, `rightsBasis:'owned'|'licensed'` and approving person. Model licences never supply source rights.

Queued identities and current approvals are rechecked under ownership before the irrevocable claim. Changing per-task state/run directories cannot reset the shared ledger. Failed, cancelled and uncertain claimed launches remain spent. This is a cooperative contract, not protection against a hostile operator replacing all policy/history.

The fixed SH0 serializer exports finite raw opacity logits plus the stored bias in FP32. It avoids the upstream FP16 sigmoid/inverse-sigmoid round trip, which can turn finite logits into infinities; it changes no model weights or neural inference precision. It retains the pinned coordinate/SH/scale/quaternion path and rejects nonfinite raw or cached values, invalid quaternions and out-of-profile fields. Per-density opacity diagnostics record finite raw ranges and cached saturation counts; no components are clipped, dropped or filled. Nonfinite payload errors identify the bounded row/property/value class. One seeded flow latent supplies separate density decodes. The decoder also uses seeded global RNG; density order is recorded and no cross-device bitwise reproducibility is promised. Record loading, preprocessing, conditioning, inference, per-density decode and unload separately. Five weights retain read leases through native retirement. Cache/temp storage additionally uses a fixed64MiB/4096-entry ceiling and CUDA cache setting, checked at stages/flow callbacks; sampling is not an OS disk quota and failed files remain retained. The fixed worker clears inherited CUDA/PyTorch overrides, blocks Python network/process/write escapes and reports phase-sampled DLLs checked against verified files, allowed Windows OS paths or exact platform-module identities. These controls are not continuous egress/module observation or an OS sandbox; actual offline/native qualification remains required.

### Platform modules and retained attempts

Endpoint software can inject DLLs outside the verified environment and allowed Windows system directories. The caller must review and register each permitted file as an exact host `tools` entry with `kind:"platform-module"`, its absolute path and SHA-256. This admits those bytes only; it does not add a directory or publisher-wide exemption. Changed bytes invalidate the pin. Resolve known platform-module requirements before spending a generative attempt.

A successful worker can still fail host package admission. Preserve its failed state, original context, claim and retained output; `generate` has no automatic resume or retry operation. Explicit post-run reconciliation uses fresh staging, unchanged source/data and a separate review record, then the canonical `verify-package` check. Operator-written metadata is untrusted until that check passes. The separate reconciliation receipt must also bind the original context, started record, claim, job and successful retired guardian outcome: `verify-package` does not reopen those historical execution records by itself. Later DLL hashes prove current file review, not the bytes loaded historically, and reconciliation never changes the original generation outcome or restores an attempt.

## Packages and review

Successful staging contains the original image, exact request, measured report, per-density PLY/artifact and `bubo-splat-package/v1`. State retains the claim and completion/failure record. Bubo independently streams actual PLY identities/counts/numeric facts before packaging; `verify-package` reopens the exact bounded inventory and candidate/revision/source/model/build/environment records. It rejects altered, missing or extra files. Verification is bound to the producer build and audited resources: retain that exact prebuilt producer and its receipt for historical checks, or use an explicitly reviewed receiving/version transition. Never relabel an old artifact as output of a newer build or reset its attempt history to accommodate a build change.

These are technical construction records. Historical conversion, calibrated physical scale, inferred hidden surfaces, full-orbit quality, real rendering/device performance, collision/animation and human acceptance remain distinct. Review original and non-input views for silhouette, holes, floaters and plausible unseen sides. No command promotes assets into a game.

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
    "timeoutSeconds": 900,
    "maxLogBytes": 16777216,
    "maxProcessMemoryBytes": 21474836480
  }
}
```

The explicit proposed process-memory value is a declaration, not measured availability, GPU-memory enforcement or an OS sandbox. CPU request validation checks schema/invariants, style, current source identity and byte bounds. It explicitly reports that image decoding and generation are unverified. Actual preprocessing must additionally bound decoded pixels, aspect ratio, intermediate resize and nonempty foreground before conditioning/model inference.

For inspection, use `bubo-splat-inspection/v1` with only `schemaVersion`, `styleSha256`, `input` (PLY path/hash) and `budgets`. JSON is case-sensitive, bounded to 64 KiB/depth16 and rejects duplicate, missing, unknown or invalid members. Current generation density variants are 32,768 and131,072; 262,144 is not automatically enabled. The seed uses the exact safe-integer host range. Source paths are relative slash paths, never URLs, parent traversal or arbitrary executables.

## Explicit PLY profile

`validate --request` also accepts the strict `bubo-splat-artifact/v1` master descriptor. It requires backend/style, original `input` and Gaussian `payload` path/hash identities, profile/count, explicit budgets, and `coordinates: source-local`, `units: uncalibrated`, `scaleConfidence: unverified`. Its ancestry requires request/environment/runtime-build/resource-catalog SHA-256 identities, exact source/model Git revisions, and an explicitly present nullable parent artifact SHA-256. Original image and payload paths must be distinct.

Artifact validation binds the descriptor bytes actually parsed to the registered reference, checks current bounded source/payload hashes, streams the PLY and compares actual count/profile. Ancestry declarations are structurally checked; their referenced builds/installations/models and construction history are not verified by this read-only operation. Results report `ancestryVerified: false` and no runtime acceptance. These declarations grant no approval and do not certify a real generation. Use verify-package for generated-package construction records; host receiving and promotion remain separate.

`triposplat-ply-sh0/v1` admits binary little-endian PLY1.0 with one bounded vertex element and exactly17 float32 properties: `x/y/z`, `nx/ny/nz`, `f_dc_0..2`, `opacity`, `scale_0..2` and `rot_0..3`. Property order may vary. Higher SH coefficients, lists, faces, unknown/duplicate properties, other encodings and trailing payloads fail explicitly.

Positions are source-local and uncalibrated; measured units and target coordinate mapping need separate evidence. Scale is natural-log standard deviation, opacity is a logit, rotation is a nonzero bounded WXYZ quaternion normalized by the receiver, and colour is RGB SH0 coefficients. The inspector reports raw position bounds, count, source hash and encoded scalar bytes. Those bytes are not a GPU allocation estimate. It streams one68-byte record at a time after validating count-times-stride, file/header/decoded limits; cancellation, nonfinite values, extreme numeric values, truncation and changed identities fail closed.
