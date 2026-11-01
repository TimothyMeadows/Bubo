using Hephaera.ProofToModel.Application;
namespace Hephaera.ProofToModel.Infrastructure;

// Public compatibility facade; guarded file mechanics are shared by native asset workflows.
public sealed class Workspace(string root, Bubo.Host.HostExecutionContext? hostContext = null) : Hephaera.AssetProduction.Workspace(root, hostContext), IWorkspace;
