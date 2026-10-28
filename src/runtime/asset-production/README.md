# Shared asset execution primitives

Workspace guards, streaming SHA-256, atomic persistence, locks, audit and bounded Windows/Linux native process ownership. Host context is supplied and verified; CanonicalEngineContext retains its compatibility name but never invokes a host resolver. Declared roots and tool identities are enforced cooperatively, without an OS sandbox claim.

Use the explicitly built `bubo` entrypoint described in the repository README with API version 1 and a validated host context. Direct legacy executable entrypoints are retired. Audited resources are owned under `resources/`; a host OpenCaw checkout, Docker or sibling repository is not a runtime prerequisite.

The preserved executable tests distinguish actual persistence/process mechanics from simulated model/engine/human producers. Native Blender and actual browser fixtures require their declared existing tool/test prerequisites and do not establish production asset acceptance. Source ancestry and MIT adaptation receipts remain in `provenance/opencaw-extraction.json` and the original vendor/REUSE files.
