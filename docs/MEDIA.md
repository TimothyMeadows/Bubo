# Local media API v1

Bubo owns execution, pinned dependencies and fixed workers. A caller supplies its own hash-bound policy, rights, tool registration, paths and budgets through `bubo-host-context/v1`. Bubo does not load OpenCaw instructions or infer a provider. Images use native stable-diffusion.cpp with FLUX.1-schnell Q8 diffusion weights; SFX uses Stable Audio Open 1.0 in a dedicated CPython 3.12.16 environment. Other media modalities are not implemented in this revision.

Use a prebuilt apphost (`bubo.exe` on Windows, `bubo` on Linux):

```text
bubo capabilities --api-version 1
bubo media setup --api-version 1 --root PROJECT --host-context CONTEXT.json --manifest SETUP.json
bubo media setup --api-version 1 --root PROJECT --host-context CONTEXT.json --manifest SETUP.json --execute
bubo media inspect --api-version 1 --root PROJECT --host-context CONTEXT.json --manifest REQUEST.json
bubo media inspect --api-version 1 --root PROJECT --host-context CONTEXT.json --manifest REQUEST.json --load true
bubo media generate --api-version 1 --root PROJECT --host-context CONTEXT.json --manifest REQUEST.json
```

Stdout contains one versioned JSON result. Diagnostics use stderr and task-owned bounded log files. `inspect` verifies files without loading models; `--load true` loads and unloads one model with zero items and no sampling. It does not consume acceptance budget. No setup operation occurs during inspection or generation.

## Host boundary

Select `policy.purpose: "local-media"`, `policy.pipeline: "LOCAL"` and a registered `policy.media: {path, sha256}`. Image requests additionally require `policy.imageStyle: {path, sha256}` registered as policy. Audio needs no invented visual style or game engine. Promotion is forbidden in a media context. Supply separate state, staging, tool and input roots; acceptance requires an explicit shared `roots.mediaState` across all relevant tasks.

References use the existing `policy`, `input` and `rights` kinds. Executables are separately registered tools with current hashes. `permissions.provisioning` must be true for explicit setup execution. It authorizes no license acceptance, generation, provider change, runtime asset promotion or driver installation by itself. Context files are cooperative caller declarations, not authenticated user approvals or an OS sandbox.

Windows loaded-module auditing admits the inventoried runtime plus OS modules in System32/WinSxS. An existing security/platform component outside those roots can be declared as an exact `tools` entry with `kind: "platform-module"`, absolute path and SHA-256. The host verifies its publisher/signature before making that declaration. Bubo rechecks the hash around queued execution and after observing the loaded module; this permission cannot shadow an inventoried dependency basename. It neither copies the component nor trusts its whole directory. Keep this host-specific prerequisite separate from model/runtime distribution.

## Pinned installation

`resources/media/catalog.json` fixes full model snapshots/components and hashes, native source/ggml/CUTLASS revisions, Python archives and the selected Windows release runtime. Each SFX environment lock includes the complete activated dependency closure, platform wheel URLs, SHA-256, wheel metadata identities and dependency declarations. No existing ComfyUI environment is used.

Setup is dry-run unless `--execute` is present. Two explicit manifest types separate installation from registration:

- `bubo-media-provision/v1` downloads/builds into a **fresh** declared project tool root. It requires task/backend, `installationRoot`, a disjoint writable `downloadRoot`, registered `bootstrapPython: {path,sha256}`, and hash-bound `licenses`. Native builds additionally require registered Git, CMake, `cudaCompiler`, and an existing `cudaRoot`. Windows setup requires registered `compilerEnvironment` (`vcvars64.bat`) and `commandShell` (System32 cmd.exe). Windows SFX uses that existing compiler environment to copy the pinned release CRT. No compiler, driver or system package is installed. A failed attempt remains preserved; use a new installation identity after reconciling it.
- `bubo-media-setup/v1` verifies and registers an existing installation. Required fields are task/backend, `receiptPath`, `modelDirectory` and `licenses`. Images supply `nativeRuntimeRoot` plus `nativeWorker` (path/hash, worker source/build hashes, revision, ggmlRevision, cutlassRevision). SFX supplies `python` (path/hash/version), `pythonBaseRoot`, `environmentRoot`, `wheelhouseRoot` and `pythonArchive`. Supply `noticeDirectory` for separate pinned runtime notices; Windows SFX requires it. Registration inventories complete roots and verifies against pinned source archives; it does not install or download anything. Register the resulting receipt as an input in a fresh context, then inspect it.

License evidence uses `bubo-media-license-evidence/v1`, `accepted: true`, explicit backend IDs and applicable `licenseIds`. The caller must actually obtain required model access and accept applicable terms. Setup never clicks through terms. A complete existing model snapshot needs no credential. For authorized gated downloads, the fixed provisioner reads an ephemeral `HF_TOKEN` environment value; it does not record it and strips authorization on non-Hugging-Face redirects. Supply credentials through a secure local mechanism, never a manifest or command argument. Generated workers neither need nor retain the token.

The installer uses only binary pinned wheels and offline pip installation (`--no-index --find-links --only-binary=:all: --require-hashes --no-deps --no-compile`). It omits unused generated entrypoint launchers; workers invoke the exact isolated interpreter. Registration checks every installed importable file against the wheel archives and the base Python archive, not self-reported package versions/RECORD alone. It admits only narrow generated metadata and verified Python launcher copies. Unverified `.pth`, `sitecustomize`, bytecode or extra loadable files are rejected. Archived base bytecode is retained unchanged. Python is invoked with `-I -B`.

The native build uses the catalog revision and fixed `worker.cmake`, existing CUDA13.2.78, Release mode, CUDA architecture120a, examples/WebP/WebM disabled, and no production server. CUDA BLAS and release CRT files belong beside the image apphost; Windows Python uses the pinned CRT beside the base/environment launchers. Preserve CUDA and Microsoft notices. The NVIDIA display driver remains a separate host prerequisite. Binary/source strings and unsigned local receipts are not signed build attestations.

Windows CUDA acceptance is the initial supported native media target. Linux dependency locks and .NET code paths are provided, but native media and dynamic library closure require actual target verification. The fixed native provisioner preserves an unverified Linux build and fails explicitly instead of claiming a complete installation. Linux Python installs must materialize safe archive links and use `venv --copies`, because production dependencies may not be symlinks. No macOS support is claimed.

## Generation requests

`bubo-media-request/v1` requires requestId, task, backend, outputDirectory, rights and installation file identities, execution settings, output budget and one to four items. IDs are bounded safe names; paths remain within the caller's declared project scope. Each request owns a new state/output identity and cannot overwrite an earlier attempt. Generated assets stay under non-runtime staging.

`rights` points to registered `bubo-media-rights/v1` evidence with task, owner, `promptRightsConfirmed: true` and `generationAllowed: true`. This is a caller declaration; it does not grant another person's identity or voice rights. Installation receipts must match the current catalog, files, runtime and platform.

Image settings are exactly `device: "cuda:0"`, `precision: "q8_0"`, `offload: "text-encoder-cpu"`. Q8 describes diffusion-weight storage, not every encoder/compute tensor. Each image item supplies prompt, explicit nonnegative int64 seed,64-aligned64..2048 dimensions,1..4steps and guidance1. Negative prompts are rejected because they do not apply to this guidance setting. Native generation metadata is checked against the request.

SFX settings are `cuda:0`, `float16` or `float32`, and `offload: "none"`. Each item supplies prompt, negativePrompt, explicit seed,1..200steps, guidance1..12, duration0.25..47seconds and loopCandidate intent. Outputs retain original amplitude as44100Hz stereo FLOAT WAV. Independent measurements report frames, peak, RMS, clipping and silence; no normalization conceals a failed waveform. Loop intent never establishes a reviewed loop.

Budgets explicitly bound maximumOutputs, maximumBytesPerOutput, maximumTotalBytes, maximumPixels and maximumAudioSeconds. The service validates actual output bytes and dimensions/duration. Nonempty SFX negative prompts require guidance greater than one; guidance one disables classifier-free guidance. Mutable execution state, cache, acceptance ledgers and outputs must remain disjoint from every immutable installed root. There is no automatic provider/device/precision fallback and no hidden extra candidate or warmup inference.

## Ownership and evidence

One worker reuses one loaded model for its bounded batch, unloads it, then exits. The same Bubo GPU guardian serializes reconstruction and media conservatively across all visible GPUs/project roots. It retains ownership until observed native descendants retire. Legacy reconstruction locks remain a compatibility guard. Cancellation, deadline, log overflow and client death request owned-child cleanup; uncertain cleanup retains the guardian lease and reports no idle claim. Independently running external apps and older Bubo versions do not cooperate with this lease. Forcibly killing the guardian itself is outside the certified cleanup guarantee; reconcile hardware/process state before any manual retry.

Versioned staged manifests bind input, policy, installation, resource and output hashes, actual settings, loading/inference/unloading timings, model-load count and pending human review. Runtime DLL observations and system-driver prerequisites are separate from source-build identity. Fixtures are labeled; they cannot certify GPU/model quality, listening review or human acceptance.

Acceptance optionally supplies `{case,kind,approval}`. The approval binds a shared six-case ledger and exactly one candidate plus at most one revision per case. Claims occur only after global/legacy ownership and input admission, immediately before model work. Failed or unknown started attempts are not refunded. Revisions retain candidate-claim ancestry. Probes sample nothing and claim nothing. Human visual/listening review and any runtime asset promotion remain separate host actions.
