# Bubo

![Bubo robotic owl](logo.png)

Bubo is a standalone .NET 10 model and asset-production runtime. It executes proof-to-model reconstruction/finishing, model-to-animation, model-to-action, read-only native Blender scene inspection, and offline image/SFX/music/voice generation through a versioned CLI. Host applications supply selected policy, permitted roots, rights/consent evidence, budgets and reviews as explicit JSON.

## Build and run

Prerequisites are an existing .NET 10 SDK and Python for source/resource verification. Git records source identity. Native production additionally requires explicitly selected pinned inference/model files and Blender 4.5 LTS. Ordinary execution performs no installation or download.

```powershell
./scripts/build.ps1 -Configuration Release
./artifacts/bubo/bubo.exe capabilities --api-version 2
./scripts/test.ps1 -Configuration Release
```

```bash
bash scripts/build.sh Release
./artifacts/bubo/bubo capabilities --api-version 2
bash scripts/test.sh Release
```

The published framework-dependent CLI, audited resources, capability identities and build receipt are under `artifacts/bubo/`. Production invokes that built artifact; it does not run `dotnet run`. Build from a recorded commit and inspect `build-info.json` for source revision, dirty-state disclosure, assembly hashes and resource-catalog identity. CI publishes Windows/Linux build artifacts. Keep Windows paths short enough for the selected native tools; path-limit failures require an explicit shorter project path.

Each downloaded CI artifact contains a complete `.tar.gz` runtime archive. Extract that archive before execution so audited hidden resources and Linux executable modes are retained. Packages include the MIT notice, extraction provenance, retained vendor notices and usage/contributor documentation; Windows/Linux CI verifies the archive before upload.

## Execution boundary

```text
bubo <workflow> <operation> --api-version 2 --root PROJECT_ROOT \
  --host-context PROJECT_CONTEXT_JSON --manifest PROJECT_MANIFEST [operation options]
```

The read-only `vfx-assets validate|inspect --package PATH` workflow checks versioned effect descriptors and their complete hash-bound dependency inventory. It preserves host STYLE/engine selection and performs no processing, rendering, installation or acceptance. See [VFX contracts](docs/VFX_ASSETS.md).

Workflows are `proof-to-model`, `model-to-animation`, `model-to-action`, `blender-inspection`, `media`, dimension-driven `floor-plan-to-model` design/construction/export and `schematic-to-model` design, independent parts, mechanical assembly, sampled readiness and portable export. `capabilities --api-version 2` advertises the supported API/backends and exact build/resource identities without a host project. API v2 results are JSON on stdout; logs are on stderr and bounded task-local files. The result's `data` contains workflow status data; exported motion/action package formats use their new Hephaera identities. API 1 is rejected; existing historical packages and approvals are not silently relabeled.

The host-context contract is implemented in `src/Bubo.Host/` and exercised by its executable tests. Its `bubo-host-context/v1` object contains the absolute project root and task, declared state/staging/tool/input/output roots, selected pipeline/engine and policy fingerprints, hash-bound rights/consent/review/input references, explicit existing native executable hashes, finite process budgets, and provisioning/promotion permissions. Permissions default false. References and input-only roots remain read-only; paths cannot escape or traverse Git metadata, aliases or reparse points. These cooperative checks and native process ownership are not an OS sandbox.

Motion/action producers require the existing engine `definitionSha256` and `registrySha256` identities, plus `contractSha256` for a non-default selection, so their exported packages remain compatible with presentation adapters. The host supplies these actual identities and corresponding evidence; Bubo does not invent definitions or select an engine.

Example host context for an owned fixture (replace hashes and paths with actual current evidence):

```json
{
  "schemaVersion": "bubo-host-context/v1",
  "projectRoot": "ABSOLUTE_PROJECT_ROOT",
  "task": "example",
  "roots": {
    "state": ".hephaera/.cache/tasks/example", "staging": ".art-staging",
    "tools": [".local/tools"], "inputs": ["inputs"], "outputs": ["assets/models"]
  },
  "policy": {
    "pipeline": "PROOF_TO_MODEL", "styleSha256": "ACTUAL_POLICY_SHA256",
    "engine": {"effectiveEngine": "THREEJS"}, "libraries": []
  },
  "references": [
    {"path": "inputs/rights.json", "sha256": "ACTUAL_RIGHTS_SHA256", "kind": "rights"}
  ],
  "tools": [],
  "budgets": {"timeoutSeconds": 1800, "maxLogBytes": 16777216},
  "permissions": {"provisioning": false, "promotion": false}
}
```

Current host examples use `.hephaera/.cache/` state keys and `hephaera-*` package identifiers. A Hephaera checkout or bootstrap is not required. The deliberate rename is exposed as execution API 2; preserve older receipts and packages byte-for-byte and use explicitly reviewed new versions when crossing this boundary. Host policy documents are interpreted by the host adapter. Bubo validates supplied data and referenced hashes; it never loads instruction documents, invokes an engine selector, discovers a host installation, or executes recipe callbacks.

## Workflows and review

- **Proof-to-model:** `init`, `doctor`, dry-run `setup`, `classify`, `prepare-proofs`, `reuse`, `preprocess`, `reconstruct`, `finish`, `review`, `record`, `validate`, `status`, `promote`. Source-only classification, six-view ancestry, explicit generation authorization, attempt limits, immutable working copies and independently reviewed runtime acceptance remain distinct gates.
- **Model-to-animation:** `init`, `doctor`, `inspect`, `prepare`, `rig`, `author`, `retarget`, `bake`, `export`, `review`, `record`, `validate`, `status`, `promote`. Preserve declared profiles, actual clip/rig hashes, owned/licensed retargeting, complete motion ranges and separate acceptance.
- **Model-to-action:** `init`, `doctor`, `inspect`, `compose`, `export`, `review`, `record`, `validate`, `status`, `promote`. Compose explicit reviewed in-place clips with bounded trims/repeats/blends/cues; gameplay authority remains outside presentation.
- **Blender inspection:** `inspect --blender ABSOLUTE_EXE`; its registered `--manifest` job contains `schemaVersion: 1`, local `scene`, `profile` and a new declared writable `output`. Profiles are `static-asset`, `rigged-actor`, `procedural-scene`, `render-scene`, and `simulation`. The existing source is opened with factory startup, disabled auto-execution and offline mode.
- **Local media:** explicit dry-run `setup`, `inspect` (optional zero-sample `--load true`), and bounded `generate`. FLUX.1-schnell Q8 images use the fixed native worker; Stable Audio Open1.0 SFX and ACE-Step1.5Turbo music use separate pinned Python environments. Music supports instrumentals and supplied lyrics with its optional planning LM disabled. Qwen3-TTS12Hz1.7B CustomVoice provides declared preset speech; Base requires exact local-reference/transcript/rights/consent for cloning, using a dedicated pinned voice environment. Shared GPU ownership covers reconstruction and media. Read [the media API and setup contract](docs/MEDIA.md) before provisioning or generation. Human output review and runtime promotion remain separate.

Read-only inspection may use `policy: {"purpose":"read-only-blender-inspection","receipt":{"path":"inspection-policy.json","sha256":"<exact hash>"}}` instead of art/engine selections. The receipt must be a registered `policy` reference. This context cannot assert pipeline, style, engine, library or media selections, enable provisioning/promotion, or execute an asset workflow. It needs no STYLE.md or ENGINE.md. Ordinary production contexts retain all existing requirements. Single-view proof diagnostics require `--mode single-view-diagnostic` explicitly on each applicable operation; this does not enable six-view production or approval reuse.

Initial assets bind canonical host selection and exact runtime build fingerprints. Changed selections, builds and legacy pre-extraction manifests require fresh affected evidence and a new asset version with retained ancestry; historical approvals are never rewritten. Native setup is dry-run unless explicit host permission and per-file licenses/rights permit provisioning. Generation cannot accept licenses, select providers or promote assets. A receipt records an assertion; it does not authenticate a human or establish visual, motion, audio or gameplay quality.

## Schematic design and mechanical readiness

`schematic-to-model` supports `init --design JSON`, `doctor`, `preview`, `validate`, `record`, `build-parts`, `assemble`, `prove-motion`, `export --author NAME`, `review` and `status`. Native steps take `--blender ABSOLUTE_EXE`. It validates a versioned mechanical assembly specification and creates six dimensioned orthographic schematics, declared internal sections, individual part sheets, an exploded assembly, connection diagrams and a hierarchical BOM. Explicit v2 adds complete assembly/rig policy sheets before approval. Reusable definitions remain distinct from independently addressable physical occurrences. Read [the schematic design contract](resources/schematic-to-model/README.md) for coordinates and examples.

Approve the exact complete package with `record --kind whole-assembly-design --reviewer HUMAN_ID --evidence REVIEW_JSON`. Design approval is separate from technical checks and model acceptance. Explicit part construction creates one editable Blender prototype and measured binary geometry per definition, then reopens every saved source in a clean process for independent verification. The native reader checks dimensions, topology, materials, instances and physical-quantity weighted budgets. See [the part construction contract](resources/schematic-to-model/PARTS.md) for supported curved geometry and enforced limits.

An approved `bubo-assembly-design/v2` can assemble those current parts, preserve every physical object and prove fixed/hinge/rotary/slider/screw movement, coupled gears/racks/pulleys/linkages and owned wire/hose/spring/belt skins. Real retained actions and independently evaluated geometry support sampled clearance, attachment and deformation evidence. Read [the assembly contract](resources/schematic-to-model/ASSEMBLY.md) for explicit pitch fits, contact zones, finite limits and diagnostic coverage. V1 remains design/parts only. Preview and capability discovery install no Blender, select no host pipeline and change no default.

Portable export preserves an editable assembly, animation-free GLB, complete component metadata/BOM and approved drawings, with diagnostic motion in a separate package. Fresh GLB imports and native triangle/skin/pose checks precede complete exterior, internal, exploded and diagnostic review views. Independent review and human model acceptance remain separate. Read [export and handoff](resources/schematic-to-model/EXPORT.md) for the preserve-only mixed `mechanical-assembly` animation profile and `init --handoff JSON`. Opt-in actual public CLI coverage is `python tests/test-schematic-export-cli.py --cli src/Bubo.Cli/bin/Release/net10.0/bubo.dll --blender ABSOLUTE_BLENDER`; test decisions are synthetic and grant no production asset acceptance.

Opt-in native QA uses `python tests/test-schematic-parts-cli.py --cli src/Bubo.Cli/bin/Release/net10.0/bubo.dll --blender ABSOLUTE_BLENDER --repeat` after an explicit build. It exercises the vehicle, opening device and curved-hull vessel through the public CLI, current design approval, bounded owned jobs, independent source reopening, reproducibility and freshness fault injection. `tests/test-schematic-parts-blender.py --blender ABSOLUTE_BLENDER` adds detailed feature and hardware fixtures. These tests label their protocol approvals synthetic and grant no actual human model/runtime acceptance or promotion.

## Verification and provenance

`provenance/hephaera-extraction.json` records the exact source repository/revision, source paths, Git blobs and working-byte SHA-256 identities for the 16 imported .NET projects, 14 fixed Blender scripts and supporting execution resources. MIT notices and the original Bubo adaptation receipts are preserved. The extraction ledger retains original attribution and import hashes; current namespaces/contracts use Hephaera under API 2.

The compiled runtime catalog checks all shipped resources before execution. Normal builds only validate it; resource changes require explicit source review and a deliberate catalog/code identity update. Blender production uses only its fixed audited bundles, immutable attempts and exact executable/script hashes.

The BCL executable suites cover actual persistence/hash/path/lock/process mechanics and label simulated inference/approval producers. The native Python fixtures exercise actual Blender finishing, motion, composition, export/re-import and review using owned engineering geometry. Their synthetic human assertions and simulated inference do not certify production model quality or license acceptance.

Actual Three.js/browser checks use the pinned development-only packages in `package-lock.json` and an existing Edge/Chrome installation. Restore those declared test dependencies with `npm ci --ignore-scripts`; `playwright-core` downloads no browser. Configure the native fixture driver with those repository-local dependencies. An unavailable real-browser check remains blocked, even when protocol simulations pass. Windows CUDA/native checks and Windows/Linux fixture coverage must be reported separately; macOS native ownership is unsupported.

The extraction preserves existing reconstruction model selections. `mediaBackends` advertises implemented direct image, SFX and music profiles. Voice is a subsequent delivery task.

## Floor-plan architecture

`floor-plan-to-model` validates dimension-driven building plans, produces complete architectural SVG packages and builds detailed empty-room architecture in Blender 4.5 LTS. Operations are `init`, `doctor`, `preview`, `validate`, `record`, `build`, `export`, `review` and `status`. Construction requires an explicitly authored v2 plan/profile and human approval of every floor, roof, elevation, section and construction-detail sheet together. Legacy v1 plans remain design-only; their approvals are never upgraded. Export seals the editable source, separate visual/collision GLBs, dimensional semantics and approved drawings. Technical verification, independent model review and human model acceptance remain separate gates.

Read [the v1 design contract](resources/floor-plan-to-model/README.md) and [v2 construction contract](resources/floor-plan-to-model/CONSTRUCTION.md) for units, floor/wall/room conventions, roofs, stairs, clearances, finite budgets and four native examples. The [portable export contract](resources/floor-plan-to-model/EXPORT.md) defines sealed payloads, independent re-import verification and complete model reviews. Every drawing in one complete candidate is reviewed together. Changes to the source, style, runtime or drawings require refreshed design evidence. Recorded reviews retain immutable snapshots of their exact original bytes.

Caller integration uses the same explicit API2 host context, registered input hashes and permitted state/staging roots as existing workflows, with pipeline `FLOOR_PLAN_TO_MODEL`. The plan's `styleSha256` must match that selected host identity. Bubo never reads host instruction files or chooses an engine. Current Hephaera integration remains separate until its accepted dependency pin is updated.

```text
bubo floor-plan-to-model init --api-version 2 --root PROJECT --host-context CONTEXT --task TASK --asset BUILDING --version VERSION --plan INPUT_PLAN
bubo floor-plan-to-model preview --api-version 2 --root PROJECT --host-context CONTEXT --manifest DESIGN_MANIFEST
bubo floor-plan-to-model doctor --api-version 2 --root PROJECT --host-context CONTEXT --manifest DESIGN_MANIFEST
bubo floor-plan-to-model validate --api-version 2 --root PROJECT --host-context CONTEXT --manifest DESIGN_MANIFEST
bubo floor-plan-to-model record --api-version 2 --root PROJECT --host-context CONTEXT --manifest DESIGN_MANIFEST --kind whole-building-design --reviewer HUMAN_ID --evidence REVIEW_JSON
bubo floor-plan-to-model build --api-version 2 --root PROJECT --host-context CONTEXT --manifest DESIGN_MANIFEST --blender ABSOLUTE_REGISTERED_BLENDER
bubo floor-plan-to-model export --api-version 2 --root PROJECT --host-context CONTEXT --manifest DESIGN_MANIFEST --blender ABSOLUTE_REGISTERED_BLENDER --author MODEL_AUTHOR
bubo floor-plan-to-model review --api-version 2 --root PROJECT --host-context CONTEXT --manifest DESIGN_MANIFEST --blender ABSOLUTE_REGISTERED_BLENDER
bubo floor-plan-to-model record --api-version 2 --root PROJECT --host-context CONTEXT --manifest DESIGN_MANIFEST --kind independent-model --reviewer REVIEWER_ID --evidence REVIEW_JSON
bubo floor-plan-to-model record --api-version 2 --root PROJECT --host-context CONTEXT --manifest DESIGN_MANIFEST --kind human-model --reviewer HUMAN_ID --evidence REVIEW_JSON
bubo floor-plan-to-model status --api-version 2 --root PROJECT --host-context CONTEXT --manifest DESIGN_MANIFEST
```

`CONTEXT` is the caller's actual registered context, not a generated approval. `init` preserves the plan as an immutable source; a revision of the same asset requires a new version and explicit `--parent-manifest`. `doctor` currently requires an initialized manifest. `record` consumes explicit review evidence bound to the complete package fingerprint and exact drawing hashes; per-floor or technical-only assertions cannot approve the building. Builds preserve editable `building.blend`, separate visual/static-dynamic collision objects, semantic floor/door hierarchy and measured receipts in immutable attempt staging. Construction does not copy assets into a game or confer model/runtime acceptance. A failed/cancelled/interrupted attempt remains evidence; it never silently falls back or selects an older build.

`doctor --blender ABSOLUTE_REGISTERED_BLENDER` checks the explicit tool/resource prerequisites without constructing a model. Native construction, export and model-view rendering require a finite host committed-memory ceiling and a runner capable of enforcing it. Current native enforcement uses 64-bit Windows owned jobs; unsupported hosts report unavailable before launch. Algorithm/preview support or a declared capability is not executable readiness. Software installation, asset promotion and game-engine traversal remain separate.

Developer tests include the native executable suite and `tests/test-floor-plan-authoring.py` pure admission guards. `--construction-domain-tests` selects the native geometry/contracts lane; `--write-construction-examples resources/floor-plan-to-model/examples-v2` regenerates the v2 engineering examples only after native validation/strict roundtrip. Source geometry proof counts are construction-stage work; prerequisite validation and freshness stages each enforce their own finite source cap and share the outer cancellation/deadline. Actual Blender source/render evidence is additional to synthetic protocol fixtures.

Portable export runs a producer and separate clean Blender imports for each GLB. Bounded raw parsing and imported world triangles, materials, semantic hierarchy and door frames must agree with approved source tolerances. Review renders the delivered visual GLB: every floor, four elevations, roof, principal section and every stair section. Section views use actual triangle clipping; open cut edges are declared. Camera metadata alone permits float32 serialization error of `2^-20` relative to camera scale; geometry and pivot tolerances remain exactly those approved in the plan. Copied clearance evidence is explicitly `construction-source`, supported by exported geometry preservation rather than a new navigation or engine test.

The worker preserves the editable source bytes and prepares transient copies with explicit float-vector corner normals. Delivered GLBs also carry every declared material IOR, including opaque materials that the bundled exporter otherwise omits. Clean imports use the pinned SDK for geometry and a fixed supported import hook for the exact delivered `NORMAL` fields. The hook verifies the SDK's positions, indices and material mapping before storing those vectors in float-vector corner attributes. It derives no replacement normals from the design. Raw and imported measurements still reject unsupported precision against the approved tolerances; the installed SDK remains unchanged.

Each export/review operation shares one cumulative output/log/work allowance and deadline across its native children. The exact Blender executable and bundled glTF module inventory are verified around execution; the workflow downloads nothing. Package/view/source changes and the latest failed or interrupted attempt invalidate current downstream evidence. Independent review must name a reviewer distinct from the sealed model author; a human decision binds the exact current independent approval and complete artifact set. Status never grants runtime acceptance or copies files into a game.

Opt-in engineering QA uses `python tests/test-floor-plan-export.py --blender ABSOLUTE_BLENDER` after an explicit build. It runs the single-storey, connected multi-storey and angled-courtyard examples through actual construction, export, clean imports and complete views. `tests/test-floor-plan-export-lifecycle.py --summary SUCCESSFUL_EXPORT_SUMMARY` then injects and restores faults only inside those owned synthetic fixtures. Its human/independent decision assertions exercise the protocol; they grant no real building or model acceptance. Both commands preserve their receipts under `.bubo/` and require no asset promotion.

The export harness selects a finite four-million-check allowance in each fresh engineering plan before initialization and synthetic approval; the five-million hard plan cap is unchanged. `tests/test-floor-plan-export-guards.py --summary SUCCESSFUL_EXPORT_SUMMARY` runs actual Windows deadline, committed-memory and log-limit checks afterward. It preserves new failed attempts, so their latest state correctly blocks older success. Ordinary CI runs pure/native protocol checks without launching Blender.
