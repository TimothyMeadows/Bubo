# Bubo

![Bubo robotic owl](logo.png)

Bubo is a standalone .NET 10 model and asset-production runtime. It executes proof-to-model reconstruction/finishing, model-to-animation, model-to-action, and read-only native Blender scene inspection through a versioned CLI. Host applications supply selected policy, permitted roots, rights/consent evidence, budgets and reviews as explicit JSON.

## Build and run

Prerequisites are an existing .NET 10 SDK and Python for source/resource verification. Git records source identity. Native production additionally requires explicitly selected pinned inference/model files and Blender 4.5 LTS. Ordinary execution performs no installation or download.

```powershell
./scripts/build.ps1 -Configuration Release
./artifacts/bubo/bubo.exe capabilities --api-version 1
./scripts/test.ps1 -Configuration Release
```

```bash
bash scripts/build.sh Release
./artifacts/bubo/bubo capabilities --api-version 1
bash scripts/test.sh Release
```

The published framework-dependent CLI, audited resources, capability identities and build receipt are under `artifacts/bubo/`. Production invokes that built artifact; it does not run `dotnet run`. Build from a recorded commit and inspect `build-info.json` for source revision, dirty-state disclosure, assembly hashes and resource-catalog identity. CI publishes Windows/Linux build artifacts. Keep Windows paths short enough for the selected native tools; path-limit failures require an explicit shorter project path.

Each downloaded CI artifact contains a complete `.tar.gz` runtime archive. Extract that archive before execution so audited hidden resources and Linux executable modes are retained. Packages include the MIT notice, extraction provenance, retained vendor notices and usage/contributor documentation; Windows/Linux CI verifies the archive before upload.

## Execution boundary

```text
bubo <workflow> <operation> --api-version 1 --root PROJECT_ROOT \
  --host-context PROJECT_CONTEXT_JSON --manifest PROJECT_MANIFEST [operation options]
```

Workflows are `proof-to-model`, `model-to-animation`, `model-to-action`, and `blender-inspection`. `capabilities --api-version 1` advertises the supported API and exact build/resource identities without a host project. API v1 results are JSON on stdout; logs are on stderr and bounded task-local files. The result's `data` contains workflow status data; exported motion/action package formats retain their existing versioned schemas.

The host-context contract is implemented in `src/Bubo.Host/` and exercised by its executable tests. Its `bubo-host-context/v1` object contains the absolute project root and task, declared state/staging/tool/input/output roots, selected pipeline/engine and policy fingerprints, hash-bound rights/consent/review/input references, explicit existing native executable hashes, finite process budgets, and provisioning/promotion permissions. Permissions default false. References and input-only roots remain read-only; paths cannot escape or traverse Git metadata, aliases or reparse points. These cooperative checks and native process ownership are not an OS sandbox.

Motion/action producers require the existing engine `definitionSha256` and `registrySha256` identities, plus `contractSha256` for a non-default selection, so their exported packages remain compatible with presentation adapters. The host supplies these actual identities and corresponding evidence; Bubo does not invent definitions or select an engine.

Example host context for an owned fixture (replace hashes and paths with actual current evidence):

```json
{
  "schemaVersion": "bubo-host-context/v1",
  "projectRoot": "ABSOLUTE_PROJECT_ROOT",
  "task": "example",
  "roots": {
    "state": ".opencaw/.cache/tasks/example", "staging": ".art-staging",
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

Historical `.opencaw/.cache/` state keys and `opencaw-*` package identifiers are retained for consumer compatibility; an OpenCaw checkout or bootstrap is not required. Host policy documents are interpreted by the host adapter. Bubo validates supplied data and referenced hashes; it never loads instruction documents, invokes an engine selector, discovers a host installation, or executes recipe callbacks.

## Workflows and review

- **Proof-to-model:** `init`, `doctor`, dry-run `setup`, `classify`, `prepare-proofs`, `reuse`, `preprocess`, `reconstruct`, `finish`, `review`, `record`, `validate`, `status`, `promote`. Source-only classification, six-view ancestry, explicit generation authorization, attempt limits, immutable working copies and independently reviewed runtime acceptance remain distinct gates.
- **Model-to-animation:** `init`, `doctor`, `inspect`, `prepare`, `rig`, `author`, `retarget`, `bake`, `export`, `review`, `record`, `validate`, `status`, `promote`. Preserve declared profiles, actual clip/rig hashes, owned/licensed retargeting, complete motion ranges and separate acceptance.
- **Model-to-action:** `init`, `doctor`, `inspect`, `compose`, `export`, `review`, `record`, `validate`, `status`, `promote`. Compose explicit reviewed in-place clips with bounded trims/repeats/blends/cues; gameplay authority remains outside presentation.
- **Blender inspection:** `inspect --blender ABSOLUTE_EXE`; its registered `--manifest` job contains `schemaVersion: 1`, local `scene`, `profile` and a new declared writable `output`. Profiles are `static-asset`, `rigged-actor`, `procedural-scene`, `render-scene`, and `simulation`. The existing source is opened with factory startup, disabled auto-execution and offline mode.

Read-only inspection may use `policy: {"purpose":"read-only-blender-inspection","receipt":{"path":"inspection-policy.json","sha256":"<exact hash>"}}` instead of art/engine selections. The receipt must be a registered `policy` reference. This context cannot assert pipeline, style, engine, library or media selections, enable provisioning/promotion, or execute an asset workflow. It needs no STYLE.md or ENGINE.md. Ordinary production contexts retain all existing requirements. Single-view proof diagnostics require `--mode single-view-diagnostic` explicitly on each applicable operation; this does not enable six-view production or approval reuse.

Initial assets bind canonical host selection and exact runtime build fingerprints. Changed selections, builds and legacy pre-extraction manifests require fresh affected evidence and a new asset version with retained ancestry; historical approvals are never rewritten. Native setup is dry-run unless explicit host permission and per-file licenses/rights permit provisioning. Generation cannot accept licenses, select providers or promote assets. A receipt records an assertion; it does not authenticate a human or establish visual, motion, audio or gameplay quality.

## Verification and provenance

`provenance/opencaw-extraction.json` records the exact source repository/revision, source paths, Git blobs and working-byte SHA-256 identities for the 16 imported .NET projects, 14 fixed Blender scripts and supporting execution resources. MIT notices and the original Bubo adaptation receipts are preserved. Source-derived namespaces and technical contracts retain their original attribution.

The compiled runtime catalog checks all shipped resources before execution. Normal builds only validate it; resource changes require explicit source review and a deliberate catalog/code identity update. Blender production uses only its fixed audited bundles, immutable attempts and exact executable/script hashes.

The BCL executable suites cover actual persistence/hash/path/lock/process mechanics and label simulated inference/approval producers. The native Python fixtures exercise actual Blender finishing, motion, composition, export/re-import and review using owned engineering geometry. Their synthetic human assertions and simulated inference do not certify production model quality or license acceptance.

Actual Three.js/browser checks use the pinned development-only packages in `package-lock.json` and an existing Edge/Chrome installation. Restore those declared test dependencies with `npm ci --ignore-scripts`; `playwright-core` downloads no browser. Configure the native fixture driver with those repository-local dependencies. An unavailable real-browser check remains blocked, even when protocol simulations pass. Windows CUDA/native checks and Windows/Linux fixture coverage must be reported separately; macOS native ownership is unsupported.

The extraction preserves existing model selections. Direct image, SFX, music and voice backends are subsequent delivery tasks; `mediaBackends` is empty in this extraction release.
