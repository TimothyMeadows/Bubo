# Bubo contributor instructions

Bubo is a standalone .NET 10 model and asset-production runtime. Read ARCHITECTURE.md and README.md. Bubo has no dependency on an OpenCaw checkout, coding-agent loop, Docker daemon, or sibling project.

Host projects select engines, art/media policy, rights, consent, budgets and required reviews. Bubo receives versioned host context and validates referenced identities; do not read instruction documents, invoke host policy resolvers, install dependencies during production, or execute recipe callbacks.

Preserve immutable inputs/working attempts, explicit licenses and provenance, fixed audited Blender scripts, cancellation and bounded owned-process execution. Technical checks do not establish human review, visual quality, motion quality or runtime asset acceptance. Package schema names inherited from OpenCaw remain compatible.

Restore, build and run focused executable tests before integrated checks. Keep temporary state under declared repository-local roots. Only explicitly authorized setup may provision pinned dependencies; no driver installation, provider fallback or implicit license acceptance. Keep logs separate from machine-readable results.
