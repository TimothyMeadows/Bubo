# Contributing

Read AGENTS.md, ARCHITECTURE.md and README.md. Use an existing .NET 10 SDK and Python. Build explicitly with scripts/build.ps1 or scripts/build.sh, then run the corresponding test script. Production calls the published CLI under artifacts/bubo; dotnet run is used only by development test runners.

Keep workflow/application/domain dependencies inward. Host context contains explicit caller decisions and hash-bound local evidence. Bubo must remain independently buildable without a host checkout, policy resolver, instruction bootstrap or coding provider.

Run the focused executable suites for changed code before native checks. tests/test-cli-contract.py exercises the public versioned entrypoint. The three tests/test-*-native-integration.py drivers use an existing Blender 4.5 LTS installation and the built Debug CLI. Set BLENDER_EXE when Blender is outside the documented default path. Test fixtures use original engineering geometry and label simulated model inference, model bytes and human review assertions explicitly.

For actual Three.js presentation checks, restore only the development packages declared in package-lock.json with npm ci --ignore-scripts and use an existing Edge/Chrome installation. Set OPENCAW_REQUIRE_MOTION_BROWSER=1 to make unavailable motion browser checks fail rather than report blocked. Browser and model installations are separate actions; tests install neither. The OPENCAW_* names and package identifiers retained in extracted fixtures preserve compatibility and imply no OpenCaw runtime dependency.

Every native job must retain bounded owned-process cancellation, logs, immutable inputs, working-copy/staging ancestry and fixed audited script hashes. Test failures remain evidence. Structural checks, synthetic approvals and a successful GLB export do not establish production visual/motion quality or grant human runtime acceptance.

Normal builds validate the committed resource catalog and its compiled hash. A deliberate resource change requires independent source review and an explicit catalog/hash update; do not make normal builds regenerate trusted hashes. Preserve the extraction ledger and MIT notices when adapting imported code. Keep exported motion/action package schemas compatible, and change versioned execution contracts explicitly when compatibility changes.

CI builds and tests Windows and Linux, then publishes the prebuilt framework-dependent CLI and exact build/resource identities. Report local native hardware/tool versions and untested platforms separately. Do not infer live-model performance from orchestration fixtures.
