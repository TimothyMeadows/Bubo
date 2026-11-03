# Bubo architecture

Bubo targets .NET 10 and uses inward dependencies: CLI -> infrastructure/application -> domain. Shared AssetProduction contains workspace guards, hashing, atomic persistence, locking, audit and bounded native process ownership. The Bubo.Host library validates the versioned caller context without depending on any workflow or Hephaera installation.

The three extracted workflow trees retain their original domain, application, infrastructure and executable fixture projects. Their current namespaces and exported motion/action identifiers use Hephaera. Execution API 2 declares this deliberate identity break; immutable API-1 imports and approvals remain historical rather than silently converted. Audited execution resources live under resources/, separate from host policy.

The media tree follows the same inward layering. Its domain defines versioned requests/capabilities, application admits host policy and budgets, and infrastructure verifies complete pinned installations and launches fixed native/Python workers. Provisioning is an explicitly authorized separate operation. The CLI composes the shared GPU guardian for reconstruction/media; it owns a global cooperative lease until native descendants retire. Media has no Hephaera dependency, game engine selector or asset-promotion authority.

The bubo entrypoint composes validated host context, constrained workspace/process ports and workflow services. It never interprets host instruction documents or calls an engine selector. References point to exact local files and hashes; native tools are explicitly selected existing executables. These cooperative guards and owned-process controls are not an OS sandbox.

Runtime resources, scripts, model/tool selections, ancestry and exported packages are hash-bound. Extracted workflow ancestry remains in provenance/hephaera-extraction.json; direct media additions have their own pinned catalog and installation identities. Setup/build and production execution are separate; production invokes an explicitly built CLI and downloads nothing.

No web framework, Docker, agent/provider coding loop, automatic human approval or automatic asset promotion is part of this runtime. Execution fixtures label simulated inference/approval separately from actual Blender and presentation evidence.

## Floor-plan design

The floor-plan-to-model tree adds pure typed dimensional contracts and bounded geometric validation in Domain, architectural compilation and renderer ports in Application, and strict serialization, deterministic SVG drawing and immutable workspace lifecycle in Infrastructure. The CLI supplies existing validated host context and exact build/style identities. Four executable source examples and adversarial geometry, preview, JSON and lifecycle fixtures exercise this design-only delivery. No Blender/model authoring is advertised until the later audited construction/export implementation exists. Engine behavior remains outside the portable design/export boundary.
