# Explicit source-canvas preprocessing

Fresh four-view init may explicitly pair `--preprocessing-profile FILE` and
`--stage-lineage FILE`. Existing manifests/build approvals are never adopted
in place. See canvas-preprocessing-profile.schema.json and
canvas-stage-lineage.schema.json. Six-view and diagnostic defaults are unchanged.

The profile selects a separately qualified BiRefNet-only executable while retaining
the base accepted toolchain and reconstruction engine. It adds only fixed
`--bg-preserve-canvas --require-gpu --gpu 0` matting flags. The owner physical GPU
is exposed through CUDA_VISIBLE_DEVICES; child0 is its local ordinal. No CPU or
alternate reconstruction fallback. Max4matting children per admitted stage.

The complete sealed bundle is <=512MiB. Optional existing CUDA13.2 dependencies
are exact registered absolute tool identities, reported separately from installed
bytes. They are never copied/downloaded implicitly. Bundle/vendor basename
shadows, links, changed hashes and unknown bundle files refuse. Matting CWD is
the sealed bundle and PATH contains only that bundle, its declared vendor parent
if needed, and the system directory. This is verified dependency selection,
not an operating-system DLL sandbox.

Lineage binds an immutable exact predecessor, directories including empty starts,
persisted history counts and limits. Predecessor state/staging and the sealed tool
bundle are input-only roots. Missing/added/removed attempts invalidate lineage;
old+new expenditure is cumulative. No old approval is copied. A zero-prior case
must explicitly bind absent predecessor and empty attempts; it grants no retry.
An extra preprocess limit2 requires retained prior1 and explicit host/user authority.
Metadata4 and model/finish1 cannot expand silently. Project policy must authenticate
the exact applicable grants, class/version and conditional model gates; JSON
assertions themselves cannot authenticate a human.

Both selectors enter native stage/approval fingerprints. Current actual mask,
straight-RGB/source-coordinate, camera and independent model/rig/motion reviews
remain required. Schemas, simulated tests and successful tool setup are not live
asset quality, human model acceptance or runtime promotion.
# Additive one-level lineage v2

The original `canvas-stage-lineage.schema.json` and v1 preprocessing ceilings remain unchanged. Optional schemaVersion2 uses `canvas-stage-lineage-v2.schema.json`: the existing fields plus exactly one `inheritedLineage:{path,sha256}`. Its prior attempts/directories/history describe only the immediate predecessor's OWN starts. The inherited reference must be the actual immediate predecessor manifest's stageLineageFile; only a v1 ancestor is admitted. Current counters are original inherited v1 starts + immediate prior own starts + fresh current starts, never the prior manifest's already cumulative total added again.

V2 requires two actual retained FAILED preprocessing stages, one from each prior version, no reconstruction/downstream attempt, unchanged source/toolchain/rights/recipe, exact live original/immediate directories/history and read-only roots. Metadata ceilings4, model/finish1 and maximum4matting children remain. Its ceiling3 authorizes only one new preprocessing stage after the two spent starts, and only with the separately bound `canvas-additional-stage-grant.schema.json` assertion. Existing authorization is the single grant reference; inherited v1 still binds its prior authorization and exact input approval. No old approvals, output stages, runtime build identity or acceptance transfer to the fresh version.

The additional-stage assertion binds task/class/source/target versions, actual immediate priorManifest and inheritedLineage refs, authorized:true, nonempty actor, stage:preprocess, consumedAttempts2/maximumAttempts3/maximumChildren4/automaticRetries:false. JSON assertions and actor strings do not authenticate human permission: the project host must independently pin a genuine new user grant before any stage. This schema creates no such grant. No arbitrary ancestry, recursive v2, donor class, reset or retry API exists. Capability `stageLineageVersion:1` remains; additive `stageLineageVersions:[1,2]` advertises parser support.

