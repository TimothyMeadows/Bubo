using Bubo.Media.Domain;

namespace Bubo.Media.Application;

/// <summary>One fixed, admitted worker owns one finite batch and exits before return.</summary>
public interface IMediaWorker
{
    Task<WorkerBatchResult> Run(MediaRequest request, PreparedInstallation installation, string attemptDirectory, CancellationToken cancellation,
        Func<CancellationToken,Task>? beforeStart = null);
}
public interface IInstallationVerifier
{
    PreparedInstallation Verify(string backend,FileIdentity receipt);
}

public sealed record PreparedInstallation(string Backend, string ReceiptPath, string ReceiptSha256, string Worker, string ModelDirectory,
    string? Python, IReadOnlyDictionary<string,string> Components, string CatalogSha256, string EnvironmentSha256,
    IReadOnlyList<Bubo.Media.Application.InstalledFile> Files,string? WorkerSha256=null,string? WorkerSourceSha256=null,string? ResourceCatalogSha256=null,
    IReadOnlyList<string>? ImmutableRoots=null);
public sealed record InstalledFile(string Path,string Sha256,long Bytes);

public sealed record WorkerBatchResult(string Protocol, ObservedExecution Execution, double LoadingSeconds, double UnloadingSeconds, WorkerOutput[] Outputs, int ModelLoads, bool Unloaded);
public sealed record ObservedExecution(string Device, string DeviceName, string Precision, string Offload, string[] ComponentPrecisions,
    string? DeviceUuid = null,string? ModelVersion = null,string? LinkedLibraryCommit = null,string[]? LoadedModules = null);
public sealed record WorkerOutput(string Id, string Path, double InferenceSeconds, int? Width, int? Height, int? SampleRate, int? Channels,
    long? Frames, double? Peak, double? Rms, double? ClippedFraction, double? SilentFraction,
    long? Seed=null,int? Steps=null,double? Guidance=null,string? Scheduler=null,FileIdentity? NativeMetadata=null);
