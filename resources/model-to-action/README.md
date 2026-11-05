# Native model-to-action v2

Action v2 composes reviewed in-place clips with exact references to reusable native VFX packages. Bubo verifies the model, compiled timing, effect/resource closure and supplied review evidence; the selected engine owns actual presentation. The existing recipe1, `hephaera-action-package/v1` and v1 player retain their original three cue kinds and cleanup rules. V2 does not run through that legacy player.

This delivery supplies native v2 data, actual Blender composition/export/re-import, portable dependency verification and acceptance/promotion gates. The Three.js/Quarks scene scheduler and actual v2 renderer integration are a separate host delivery. Technical Blender motion/anchor captures are not selected-engine effect captures. Native support does not establish browser, GPU, MAUI/device, visual quality or human acceptance.

## Contracts

| Data | Contract |
| --- | --- |
| Recipe | [action-recipe-v2.schema.json](action-recipe-v2.schema.json), integer `schemaVersion: 2` |
| Runtime package | [action-package-v2.schema.json](action-package-v2.schema.json), `hephaera-action-package/v2` |
| Private manifest | [manifest-v2.schema.json](manifest-v2.schema.json), `hephaera-model-action/v2` |
| Engine profile | [effect-profile-v1.schema.json](effect-profile-v1.schema.json), exact `hephaera-three-vfx-profile/v1` |
| Source rights | [effect-rights-v1.schema.json](effect-rights-v1.schema.json), `bubo-vfx-source-rights/v1` |
| Source production review | [effect-production-review-v1.schema.json](effect-production-review-v1.schema.json), `bubo-vfx-source-production-review/v1` |
| Portable correspondence | [effect-resource-correspondence-v1.schema.json](effect-resource-correspondence-v1.schema.json), `bubo-vfx-resource-correspondence/v1` |
| Selected-engine receiving packet | [action-effect-receiving-v1.schema.json](action-effect-receiving-v1.schema.json) |
| Runtime assertion/review | [action-runtime-evidence-v2.schema.json](action-runtime-evidence-v2.schema.json), [action-runtime-review-v2.schema.json](action-runtime-review-v2.schema.json) |

The shared definitions are in [action-effects-v2.schema.json](action-effects-v2.schema.json). Schemas describe structure; native admission additionally verifies actual bytes, graph correspondence, aliases, ownership, cumulative limits, timing and current evidence. A syntactically valid hash or approved assertion alone is insufficient. Recipe data cannot select scripts, shaders, tools, providers, loaders or installation/download behavior.

Only reviewed motion-package/v1 `skinned`, `articulated` and `rigid` profiles are admitted. Mechanical-assembly action composition and Phaser GLB handoffs remain unavailable. The v2 effect bridge requires the explicitly selected THREEJS engine and exact contract/definition/registry identities. This workflow neither selects an engine nor changes STYLE or its primary pipeline.

## Inspect source effects before initializing an action

Each `effectSources` entry names a stable local owner ID, exact native package/hash, selected `effectId`, profile/hash, complete resource correspondences, rights and production-review references. Native package/v1 and package/v2 retain their distinct resource semantics. Bubo derives **all** native resources from the package; the recipe cannot replace that inventory with an authored list. One package may be selected by several source IDs/effects, with independently counted ownership and copies.

Create a source rights assertion over the exact original PNG/GLB inputs. Prepared resources require rights coverage for their original preparation sources; source-free mathematical masks require their applicable owned assertion. A source production review names its author, independent reviewer, reason and exact local evidence. It binds the selected effect, native package/profile/resource/preparation/correspondence/rights inputs and actual evidence. Bubo checks these assertions and hashes; it does not authenticate a reviewer or perform artistic review.

Use an explicit pending production-review draft to calculate its exact subject:

```text
bubo model-to-action inspect-effects --api-version 2 --root PROJECT_ROOT \
  --host-context CURRENT_CONTEXT_JSON --recipe inputs/action-v2.json
```

The draft uses `decision: pending`, a lowercase 64-character placeholder subject and real bounded evidence references. The read-only result supplies each `subjectSha256`, inspected inventory and `accepted: false`. It needs no action manifest or Blender, creates no action stage or preparation candidate, and grants no review/acceptance allowance. Complete the actual independent source review, write a new approved review file with that exact subject, update its reference hash and initialize the action normally. A draft never produces an approved effect catalog.

The subject excludes the production-review file/path/bytes to avoid self-hashing. It includes the selected `effectId`; changing the selected effect requires its own review subject. The action's source alias ID is excluded so the same reviewed effect may have multiple presentation owners. Full SourceClosure then includes the final approved review bytes. Rights inputs likewise do not self-hash.

Optional `preparation` identifies the **native** sealed `preparation.json`, not a host wrapper receipt. Its SHA must equal `profile.preparationSha256`. Verification reopens the original output, request, sources, version claim, task, policy and current native build, reproduces the pixels and checks exact atlas ancestry. An explicit `preparationContext` is required to reuse a preparation from another task/custom state root. It must preserve the original task/context, false provisioning/promotion and no tools, and be a registered current input. Every original reference/root must already be readable through the current caller. The verifier intersects both read scopes, permits no writes/locks, and never discovers a context or relabels a seal. Cross-project/external-root reuse and stale native builds are unsupported. Keep the original context and inputs local and current.

## Private source inventory and portable runtime inventory

The action manifest and immutable `original/effects/<source-id>/` snapshots retain full source package/profile/resources, rights, production review, local evidence and applicable preparation/context/claim ancestry. Every original source path is still checked after snapshotting. These are durable private non-runtime artifacts.

The exported action's `effectSources` contains only:

- Exact native package bytes, every declared PNG/GLB resource and exact production-profile bytes.
- A constrained canonical correspondence JSON for every native resource, binding source/decoded digests, independent assertion, bounded dimensions/counts and `sourceEvidenceSha256`.
- Hash ancestry for SourceClosure, rights, production review and optional native preparation. The private evidence bytes/paths are absent from the canonical correspondence.

Original preparation PNGs, requests, starts, claims, seals, contexts, raw rights/review files and captures are not runtime payloads. Native package-relative resource layout is preserved; outer action references point only at the owning portable closure. RuntimeClosure hashes stable logical source/effect identities, local portable names, byte identities and ancestry, excluding the destination prefix. Native promotion may therefore rebind every outer path while preserving that closure identity; original and promoted action-package hashes remain separate.

Sources, files, dependency bytes and JSON bytes have finite aggregate limits across owners. JSON accounting includes semantic JSON roles even when an original preparation input has an unusual filename. `maxRuntimeBytes` bounds the delivered model, all native effect package/resource/profile/correspondence bytes and the serialized outer action descriptor together. It does not reinterpret a per-package cap as a scene budget. Decoded texture/buffer and measured geometry reservations are also checked against the exact profile. Native PNG header/pixel checks and mesh envelope/correspondence assertions have their stated limits; no general decoder or renderer is installed.

The initial Three.js profile reserves owned texture copies and every procedural/mesh/ring/beam/trail vertex, index and buffer allocation conservatively across selected layers and the complete action cue union, including tails. It does not optimize the reservation by assuming cues cannot overlap. Native layer vertex caps describe requested logical geometry; profile/action caps include the selected helper's fixed Quarks batch floors. These are allocation estimates for that fixed profile, not measured GPU memory or performance. For example, one trail particle with eight history points requests 16 vertices/48 indices but reserves 20,000 vertices/60,000 indices and 1,600,152 buffer bytes under the current 10,000-point batch floor. A ring with 12 segments and one particle reserves 26 vertices/72 indices and 65,120 buffer bytes. Choose explicit budgets for those allocations instead of reducing the native logical cap to disguise preallocation.

## Timing and ownership

Cues reference actual exported anchors and the existing unique marker/step-offset address form. Transforms declare meters, +Y up, +Z forward, right handed coordinates, normalized xyzw orientation and positive uniform scale/parent policy. No marker timeout, guessed bone, mirrored/nonuniform parent fallback or coordinate reinterpretation is supplied.

At the exact release boundary, the separate host runtime must sample the **delivered composite clip** and its actual exported anchor. Blender composition bakes at the declared plan FPS; native verification compares the baked grid and actual delivered review-boundary poses. This sampling does not certify preservation of original source excursions between bake keys, including subframe motion. Exact release timing is not a claim that unsampled original motion was preserved.

Each profile layer owns its declared local/world emission space. Safe per-layer overrides only reduce size/color/emission; they cannot add resources or raise lifetime, history or particle limits. Cue end stops normal emission. Compiled per-layer start/stop/retire facts and action retirement bound the drain; no layer is silently stretched or truncated. Cancellation/replacement/reset/disposal always retire the generation. Presentation may drain after releasing rig-write ownership, with explicit pause/attachment policy and resource leases still counted.

Fixed world points remain action-start snapshots. Only a declared beam may use continuous gameplay-supplied endpoints with exact generation, age/rate, timestamp, sampling and invalid-input retirement rules. Trails require bounded retained samples and reset on teleport/discontinuity. A rehearsed visual arrival/impact supplies no hit, damage, collision, targeting or movement authority.

## Receiving evidence and promotion

V2 independent and human decisions require actual selected-engine receiving evidence in addition to the existing action selection. The receiving packet binds model/package/closure/source/profile/preparation/correspondence/decoded-resource hashes, the full selected engine/build and current driver/scheduler/particle/VFX/composer/library/scene/camera/settings/device inputs. Runtime modules are referenced and hashed as evidence; Bubo never executes supplied module paths.

Each action needs a complete normal-speed video and exact PNG samples for its declared capture cadence plus all source-step/marker/cue/layer stop/retire boundaries, maximum occupancy and final cleanup. Every selected layer needs isolated, dark, light and combined-action captures. Lifecycle, concurrent actors plus ambient effects, target/trail policies and cleanup must pass under the same packet. Performance requires at least five matched baseline/candidate runs with CPU submission measurements labeled when GPU timing is unavailable. Supplied status strings remain assertions; pixel quality, actual hardware behavior and human decisions are separate responsibilities.

Blender-only/proxy packets, sparse or nearest-frame coverage, missing layers, leaked owned resources, changed cameras/drivers/resources/captures and stale/transplanted identities cannot satisfy these native gates. Existing v1 runtime receipts are not relabeled as v2 evidence. Without the separately delivered actual engine owner/captures, v2 runtime review remains not-run and promotion cannot proceed.

The existing native `Acceptance.cs` remains the sole action promotion owner. With explicit current host promotion permission and current action/independent/human evidence, it copies the complete bounded runtime closure into a new pending destination, rebinds every model/effect reference, verifies all hashes and the exact pending inventory, rechecks full private source/stage/evidence freshness, then atomically moves to the new `assets/models/...` directory. Failed pending evidence is retained; existing destinations are never overwritten. This documentation, an export, a review subject or synthetic test fixture grants no asset acceptance or promotion authorization.

The [original v2 recipe/profile examples](recipes/README.md) use illustrative names and placeholder hashes. Executable test fixtures distinguish actual original Blender construction/export/re-import from synthetic rights/review/runtime assertions. None establish production human acceptance.
