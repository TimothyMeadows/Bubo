using Bubo.Vfx.Domain;

namespace Bubo.Vfx.Application;

/// <summary>Bytes observed from the same bounded stream used to compute the digest.</summary>
public sealed record VfxObservedResource(string Id, string Kind, string Path, string ProjectPath, long Bytes, string Sha256);

public sealed record VfxRequirements(VfxEngineIdentity Engine, string StyleSha256,
    IReadOnlyList<string> LayerKinds, IReadOnlyList<string> ResourceKinds);

/// <summary>Contract/resource inspection is separate from decoded content or runtime readiness.</summary>
public sealed record VfxCapabilities
{
    public bool ContractValidationAvailable => true;
    public bool DependencyInspectionAvailable => true;
    public bool ResourceEnvelopeValidationAvailable => true;
    public bool ResourceContentValidationAvailable { get; init; }
    public bool ResourcePreparationAvailable => true;
    public bool RuntimeRenderingAvailable => false;
    public bool ActionCompositionAvailable => false;
    public bool VideoDecodingAvailable => false;
    public bool MediaGenerationAvailable => false;
    public bool PromotionAvailable => false;
    public string ResourceContentStatus => ResourceContentValidationAvailable
        ? "v2-atlas-png-decoded-mesh-envelope-only" : "envelope-only-not-decoded";
}

public sealed record VfxInspectionResult(VfxPackage Package, string PackagePath, string PackageSha256,
    long PackageBytes, IReadOnlyList<VfxObservedResource> ObservedResources, ValidationReport Validation,
    VfxRequirements Requirements, VfxCapabilities Capabilities)
{
    public bool IsValid => Validation.IsValid;
}
