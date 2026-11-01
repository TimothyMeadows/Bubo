using Hephaera.ProofToModel.Application;
namespace Hephaera.ProofToModel.Infrastructure;

// Preserve the proof runtime API while sharing bounded owned-process execution.
public sealed class NativeProcess : Hephaera.AssetProduction.NativeProcess, INativeProcess;
