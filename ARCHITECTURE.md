# Bubo architecture

Bubo targets .NET 10 and uses inward dependencies: CLI -> infrastructure/application -> domain. Shared AssetProduction contains workspace guards, hashing, atomic persistence, locking, audit and bounded native process ownership. The Bubo.Host library validates the versioned caller context without depending on any workflow or OpenCaw installation.

The three extracted workflow trees retain their original domain, application, infrastructure and executable fixture projects. Their OpenCaw namespaces and exported motion/action schema identifiers preserve source and package compatibility; they do not imply a host dependency. Audited execution resources live under resources/, separate from host policy.

The bubo entrypoint composes validated host context, constrained workspace/process ports and workflow services. It never interprets host instruction documents or calls an engine selector. References point to exact local files and hashes; native tools are explicitly selected existing executables. These cooperative guards and owned-process controls are not an OS sandbox.

Runtime resources, scripts, model/tool selections, ancestry and exported packages are hash-bound. Algorithms and model selections remain those of the extraction baseline in provenance/opencaw-extraction.json. Setup/build and production execution are separate; production invokes an explicitly built CLI and downloads nothing.

No web framework, Docker, agent/provider coding loop, automatic human approval or automatic asset promotion is part of this runtime. Execution fixtures label simulated inference/approval separately from actual Blender and presentation evidence.
