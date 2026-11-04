# VFX package contracts and dependency inspection

`vfx-assets` introduces a data-only `bubo-vfx-package/v1` contract. Its two operations, `validate` and `inspect`, both validate the complete declaration and inspect the declared local dependency bytes. They do not create lifecycle state or write output files. Existing MODEL_TO_ACTION v1 recipes, cue kinds, exported packages and acceptance semantics remain unchanged.

```text
bubo vfx-assets validate --api-version 2 --root PROJECT_ROOT --host-context CONTEXT_JSON --package INPUT_PACKAGE_JSON
bubo vfx-assets inspect --api-version 2 --root PROJECT_ROOT --host-context CONTEXT_JSON --package INPUT_PACKAGE_JSON
```

Use an explicitly built Bubo CLI. The package must be registered with kind `input` and its exact SHA-256 in the supplied `bubo-host-context/v1`; merely placing it beneath a declared input root is insufficient. Its style hash and all four engine identity fields must exactly match the full art/engine host context. A Blender-inspection-only or media-only context cannot supply this authority. The host context and references are checked for freshness before and after inspection. Bubo validates supplied selections and referenced bytes without interpreting host instruction documents or selecting an engine.

Success returns an API 2 `bubo-execution-result/v1` envelope with `status: "succeeded"`. Semantic failures return `status: "failed"`, a nonzero exit code, and `data.validation.issues` containing `code`, `path` and `message`. Malformed input, missing/changed/unsafe dependencies, host binding failures and unavailable operations fail with a nonzero exit code. The structured result remains on stdout; diagnostic logs use stderr. A semantic invalid declaration is reported before opening its declared resource paths.

## Package shape

JSON property names and string discriminators are case-sensitive. Every field below is required, including zero capacities and empty resource-reference arrays where permitted. Unknown, duplicate, missing, null and wrong-case properties are rejected at every object level. Numeric strings, fractional counts, integer overflow, null array elements and executable recipe fields are rejected. There are no callbacks, shader strings, tool selections, network inputs or import instructions in this schema.

```json
{
  "schemaVersion": "bubo-vfx-package/v1",
  "id": "owned-effect",
  "version": "v1",
  "styleSha256": "<64 lowercase hexadecimal characters>",
  "engine": {
    "id": "THREEJS",
    "contractSha256": "<exact host contract hash>",
    "definitionSha256": "<exact host definition hash>",
    "registrySha256": "<exact host registry hash>"
  },
  "budgets": {
    "maxPackageBytes": 65536,
    "maxDependencyBytes": 4096,
    "maxTotalDependencyBytes": 8192,
    "maxDependencies": 2,
    "maxEffects": 1,
    "maxLayers": 2,
    "maxParticles": 32,
    "maxVertices": 128,
    "maxDrawCalls": 4,
    "maxTrailPoints": 16,
    "maxDurationSeconds": 4
  },
  "resources": [
    {
      "id": "spark",
      "kind": "texture-png",
      "path": "textures/spark.png",
      "sha256": "<exact file hash>",
      "bytes": 128
    }
  ],
  "effects": [
    {
      "id": "cast",
      "durationSeconds": 2,
      "layers": [
        {
          "id": "charge",
          "kind": "sprite",
          "startSeconds": 0,
          "durationSeconds": 1,
          "resourceIds": ["spark"],
          "maxParticles": 16,
          "maxVertices": 32,
          "maxDrawCalls": 1,
          "maxTrailPoints": 0
        }
      ]
    }
  ]
}
```

The placeholders above are explanatory and do not constitute a valid package. Hashes and byte counts must come from the actual selected local files. Identifiers use 1–80 ASCII letters, digits, periods, underscores or hyphens and start with a letter or digit. Resource IDs, effect IDs, per-effect layer IDs and resource paths cannot collide through letter casing. References resolve exact resource IDs, with no case folding. Every declared resource must be referenced, and every reference must be declared; unrelated filesystem siblings are not imported.

## Resources and layer bounds

Resources admit `texture-png` with a `.png` extension or `mesh-glb` with `.glb`. A resource path is relative to the package directory and uses canonical forward slashes. Traversal, absolute dependency paths, Git metadata, device names, alternate streams, ambiguous separators and trailing dot/space aliases are rejected. Package arguments can use a safe absolute project-local path; reports use project-relative identities. Symlinks and reparse points are rejected in package/dependency path components. The package cannot alias a dependency, and dependencies cannot alias one another through their checked path identities.

Inspection streams dependencies under finite byte limits, verifies actual byte count and SHA-256, and checks a bounded file envelope. PNG checks cover the signature, IHDR/chunk structure, CRC, order and termination, with at most 65,536 chunks. GLB checks cover the v2 container/chunk structure, a JSON declaration bounded to 1 MiB and depth 32, one internal buffer with an optional BIN chunk, and buffer/view bounds. GLB `uri` properties, including data URIs, and extension declarations are rejected rather than leaving untracked dependencies outside the declared inventory. These checks do not decode PNG pixels, validate compressed image content, load model geometry or certify a renderable asset.

The declared layer kinds are `sprite`, `ring`, `mesh`, `burst`, `projectile`, `trail`, `impact`, `ground-indicator` and `beam`. Declaring a kind does not provide its renderer. A `mesh` layer requires exactly one GLB and may also reference PNGs. `projectile` and `impact` require visual resources and admit at most one GLB. Other kinds accept PNG resources only; `sprite` requires at least one. Procedural kinds can have an empty reference array when the overall package still has a nonempty, fully referenced inventory.

Each effect has a positive finite duration. Each layer has a nonnegative finite start and positive finite duration wholly inside that effect. Vertex and draw-call limits are positive; particle and trail-point limits can be zero. Capacities and layer counts are summed conservatively across every layer, including layers whose intervals do not overlap. Effect durations are also summed against the duration budget. These are declaration bounds; actual overdraw, memory use, frame cost, accessibility and visual readability require later target evidence.

All budgets are explicit and positive. A package may narrow these absolute ceilings:

| Boundary | Absolute ceiling |
| --- | ---: |
| Package JSON | 1 MiB; depth 32 |
| One dependency | 1 GiB |
| All dependencies | 4 GiB |
| Dependencies / effects / layers | 1,024 / 64 / 1,024 |
| Particles / vertices | 1,048,576 / 16,777,216 |
| Draw calls / trail points | 65,536 / 1,048,576 |
| Aggregate effect duration | 3,600 seconds |

Inspection checks cancellation before admission, during streamed work and before return. It rechecks package identity after reading dependencies. The existing host-context reference hashing runs separately from these VFX byte/cancellation bounds. Filesystem/path guards and host freshness checks are cooperative snapshots; they are not an operating-system sandbox, physical hardlink identity guarantee or a transaction against hostile concurrent mutation. The result binds the bytes observed in that inspection and grants no durable acceptance or approval. Consumers must retain immutable versions and revalidate changed inputs.

## Available evidence and limits

The result includes the exact package path/hash/size, observed resource identities, semantic issues, declared layer/resource requirements, and capability flags. Contract validation and dependency/envelope inspection are available. Full resource-content validation, atlas/sheet/sequence preparation, video decoding, media generation, renderer execution, action composition, promotion and runtime acceptance remain unavailable. No native worker, decoder, image provider, engine adapter, renderer or tool installation is invoked. A new engine, resource processor or action contract requires its own delivery and evidence.

The executable suite uses a newly generated 1×1 RGBA PNG and a minimal owned GLB container. It covers strict JSON admission, semantic/resource closure and finite budgets; missing, changed, wrong-size, malformed, escaping, case-colliding and linked dependencies; exact host input/style/engine bindings; cancellation; and absence of filesystem writes. The optional CLI path also exercises actual public success and failure envelopes. Fixtures and their synthetic host policy data establish no production license acceptance, visual quality, human review, gameplay authority or browser/MAUI/device support.

```powershell
dotnet run --project src/runtime/vfx-assets/Tests/Vfx.Tests.csproj --configuration Release
dotnet run --project src/runtime/vfx-assets/Tests/Vfx.Tests.csproj --configuration Release -- --cli src/Bubo.Cli/bin/Release/net10.0/bubo.dll
```

These development commands use the existing .NET 10 SDK and BCL only; no external test packages are required. The CLI argument selects an existing built artifact. A missing native/browser/device check remains not run and cannot receive a verified support claim.
