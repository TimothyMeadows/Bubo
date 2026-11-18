using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Splats.Domain;
using Bubo.Splats.Infrastructure;
using Hephaera.AssetProduction;

namespace Bubo.Splats.Tests;

/// <summary>Real registration/content verification over tiny inert archives; no interpreter or native process runs.</summary>
internal static class InstallationTests
{
    internal static void Run()
    {
        var cancelled = new InstallationFixture();
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            Check.Throws<OperationCanceledException>(() => cancelled.Register(cancellation.Token), "cancelled registration writes no receipt");
            Check.That(!File.Exists(cancelled.Full("state/installation.json")), "cancelled registration retains no success record");
        }

        var overlap = new InstallationFixture();
        overlap.ChangeRequest(value => value with { EnvironmentRoot = value.PythonBaseRoot });
        Check.Throws<InvalidOperationException>(() => overlap.Register(), "overlapping immutable registration roots rejected");

        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            var unavailable = new InstallationFixture();
            var plan = JsonSerializer.SerializeToNode(unavailable.Service().Execute("inputs/request.json", false, default), SplatJson.Options)!;
            Check.That(!plan["installationVerified"]!.GetValue<bool>() && !plan["registrationPlatformSupported"]!.GetValue<bool>(), "unsupported host returns metadata only");
            Check.Throws<PlatformNotSupportedException>(() => unavailable.Register(), "native registration does not fall back on unsupported hosts");
            Console.WriteLine("Archive-derived registration fixtures skipped: Windows x64 registration profile required; no native execution attempted.");
            return;
        }

        var valid = new InstallationFixture();
        var registered = JsonSerializer.SerializeToNode(valid.Register(), SplatJson.Options)!;
        Check.That(registered["status"]!.GetValue<string>() == "registered-pending-native-inspection"
            && !registered["nativeReadinessVerified"]!.GetValue<bool>() && !registered["ancestryVerified"]!.GetValue<bool>(),
            "actual tiny-file registration succeeds without native readiness or historical ancestry claims");
        valid.AdmitReceipt();
        var prepared = valid.Verify();
        Check.That(prepared.Python == valid.Full("tools/environment/Scripts/python.exe")
            && prepared.Inputs.Count(identity => identity.Path.StartsWith("tools/models/", StringComparison.Ordinal)) == 5,
            "real receipt verification reloads the five-weight installation and exact launcher");
        var originalReceipt = File.ReadAllBytes(valid.Full("state/installation.json"));
        Check.Throws<InvalidOperationException>(() => valid.Register(), "registration cannot overwrite its immutable receipt");
        Check.That(originalReceipt.SequenceEqual(File.ReadAllBytes(valid.Full("state/installation.json"))), "rejected overwrite preserves receipt bytes");

        valid.Flip("tools/environment/Lib/site-packages/fixture/__init__.py");
        Check.Throws<InvalidOperationException>(() => valid.Verify(), "receipt verification rejects changed installed bytes");

        var missingNotice = new InstallationFixture();
        File.Delete(missingNotice.Full("tools/notices/DINOv3-LICENSE.md"));
        // Rebind the complete human-reviewed *actual* inventory. The catalog must still require DINOv3.
        missingNotice.RefreshDescriptors();
        Check.Throws<InvalidOperationException>(() => missingNotice.Register(), "catalog-required DINOv3 notice cannot be waived by a true human review flag");
        Check.That(!File.Exists(missingNotice.Full("state/installation.json")), "missing component notice cannot produce a registration receipt");

        var modifiedEnvironment = new InstallationFixture();
        modifiedEnvironment.Flip("tools/environment/Lib/site-packages/fixture/__init__.py");
        Check.Throws<InvalidOperationException>(() => modifiedEnvironment.Register(), "fresh registration derives installed package bytes from the wheel rather than a generated inventory");

        var systemSite = new InstallationFixture();
        systemSite.Write("tools/environment/pyvenv.cfg", "home = " + systemSite.Full("tools/base") + "\ninclude-system-site-packages = true\n");
        Check.Throws<InvalidOperationException>(() => systemSite.Register(), "system site packages cannot enter the dedicated registration");

        var alteredWheel = new InstallationFixture();
        alteredWheel.Flip("tools/wheels/fixture-0.0-py3-none-any.whl");
        Check.Throws<InvalidOperationException>(() => alteredWheel.Register(), "modified wheel archive is rejected against its audited pin");

        var alteredWeight = new InstallationFixture();
        alteredWeight.Flip("tools/models/weight0.safetensors");
        Check.Throws<InvalidOperationException>(() => alteredWeight.Register(), "model bytes must match the complete catalog set");

        var extra = new InstallationFixture();
        extra.Write("tools/environment/unrecorded.dll", "SYNTHETIC; never loaded");
        Check.Throws<InvalidOperationException>(() => extra.Register(), "unrecorded environment files cannot be legitimized by a new inventory");

        var extraAfterRegistration = new InstallationFixture();
        extraAfterRegistration.Register(); extraAfterRegistration.AdmitReceipt();
        extraAfterRegistration.Write("tools/source/unrecorded.py", "# SYNTHETIC; never imported\n");
        Check.Throws<InvalidOperationException>(() => extraAfterRegistration.Verify(), "independent receipt reload rejects extra installed source files");
        Console.WriteLine("Installation fixtures registered only inert synthetic files via C# byte verification. No Python, software installation, model, native import, GPU or real licence acceptance ran.");
    }

    sealed class InstallationFixture
    {
        const string TaskId = "synthetic-registration";
        static readonly string[] ComponentNoticeNames = ["TripoSplat-MIT.txt", "DINOv3-LICENSE.md", "FLUX2-Apache-2.0.txt", "BiRefNet-MIT.txt", "NOTICE.txt"];
        static readonly string[] RuntimeNoticeNames = ["VS-Redist.txt", "VS-ThirdPartyNotices.txt"];
        static readonly string[] RuntimeNames = ["msvcp140.dll", "msvcp140_codecvt_ids.dll", "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140_atomic_wait.dll", "vcruntime140_threads.dll", "vcomp140.dll"];
        static readonly string[] LicenseIds = ["triposplat", "dinov3", "flux2-autoencoder", "birefnet", "environment"];
        readonly JsonArray artifacts;
        bool receiptAdmitted;
        SplatInstallationRequest request;
        public string Root { get; }

        public InstallationFixture()
        {
            var temporary = Environment.GetEnvironmentVariable("TMPDIR") ?? Environment.GetEnvironmentVariable("TEMP") ?? ".bubo/test-temp";
            Root = Path.Combine(Path.GetFullPath(temporary), "splat-registration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root); // Fixtures are retained; never recursively delete a computed path.
            foreach (var directory in new[] { "inputs", "tools/models", "tools/source", "tools/base", "tools/environment", "tools/wheels", "tools/archive", "tools/notices" })
                Directory.CreateDirectory(Full(directory));

            var weights = new JsonArray();
            for (var index = 0; index < 5; index++)
            {
                var name = "weight" + index + ".safetensors"; Write("tools/models/" + name, "SYNTHETIC WEIGHT " + index);
                weights.Add(Pin("tools/models/" + name, name));
            }
            var sources = new JsonArray();
            foreach (var name in new[] { "model.py", "triposplat.py", "LICENSE" })
            {
                Write("tools/source/" + name, "# SYNTHETIC; never executed: " + name + "\n"); sources.Add(Pin("tools/source/" + name, name));
            }

            var pip = Wheel("pip"); var dependency = Wheel("fixture");
            Write("tools/wheels/fixture-0.0-py3-none-any.whl", dependency);
            var baseFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["python.exe"] = Bytes("SYNTHETIC BASE; never executed"),
                ["python312.dll"] = Bytes("SYNTHETIC DLL; never loaded"),
                ["Lib/venv/scripts/nt/python.exe"] = Bytes("SYNTHETIC VENV; never executed"),
                ["Lib/venv/scripts/nt/pythonw.exe"] = Bytes("SYNTHETIC WINDOWED VENV; never executed"),
                ["Lib/ensurepip/_bundled/pip-0.0-py3-none-any.whl"] = pip
            };
            Write("tools/archive/python.tar.gz", PythonArchive(baseFiles));
            foreach (var file in baseFiles) Write("tools/base/" + file.Key, file.Value);
            Write("tools/environment/Scripts/python.exe", baseFiles["Lib/venv/scripts/nt/python.exe"]);
            Write("tools/environment/Scripts/pythonw.exe", baseFiles["Lib/venv/scripts/nt/pythonw.exe"]);
            Write("tools/environment/pyvenv.cfg", "home = " + Full("tools/base") + "\ninclude-system-site-packages = false\n");
            InstallWheel(pip); InstallWheel(dependency);
            var runtimeFiles = new JsonArray();
            foreach (var name in RuntimeNames)
            {
                var content = Bytes("SYNTHETIC CRT; never loaded: " + name);
                Write("tools/base/" + name, content); Write("tools/environment/Scripts/" + name, content);
                var pin = Pin("tools/base/" + name, name); pin["backends"] = new JsonArray("ace-step-turbo"); runtimeFiles.Add(pin);
            }
            foreach (var name in ComponentNoticeNames.Concat(RuntimeNoticeNames))
                Write("tools/notices/" + name, "SYNTHETIC fixture notice; grants no actual rights: " + name);

            var wheelPin = Pin("tools/wheels/fixture-0.0-py3-none-any.whl", "fixture-0.0-py3-none-any.whl");
            artifacts = new JsonArray(new JsonObject { ["name"] = "fixture", ["version"] = "0.0", ["filename"] = "fixture-0.0-py3-none-any.whl",
                ["size"] = wheelPin["bytes"]!.GetValue<long>(), ["sha256"] = wheelPin["sha256"]!.GetValue<string>() });
            var python = Pin("tools/archive/python.tar.gz", "python.tar.gz"); python["version"] = "3.12.16";
            var catalog = new JsonObject { ["schemaVersion"] = "bubo-splat-catalog/v1", ["backend"] = "triposplat", ["weights"] = weights,
                ["source"] = new JsonObject { ["files"] = sources }, ["environmentCandidates"] = new JsonObject { ["win-amd64"] = new JsonObject { ["python"] = python, ["artifacts"] = artifacts.DeepClone() } },
                ["licenseComponents"] = new JsonArray(LicenseIds.Select(id => (JsonNode)new JsonObject { ["id"] = id }).ToArray()),
                ["notices"] = new JsonArray(ComponentNoticeNames.Select(name => (JsonNode)Pin("tools/notices/" + name, name)).ToArray()) };
            WriteJson("bundle/resources/image-to-splat/catalog.json", catalog);
            WriteJson("bundle/resources/media/catalog.json", new JsonObject { ["windowsRuntime"] = new JsonObject { ["files"] = runtimeFiles,
                ["notices"] = new JsonArray(RuntimeNoticeNames.Select(name => (JsonNode)Pin("tools/notices/" + name, name)).ToArray()) } });
            WriteJson("bundle/resources/catalog.json", new JsonObject { ["schemaVersion"] = 1, ["files"] = new JsonObject {
                ["resources/image-to-splat/catalog.json"] = Identity("bundle/resources/image-to-splat/catalog.json").Sha256,
                ["resources/media/catalog.json"] = Identity("bundle/resources/media/catalog.json").Sha256 } });
            Write("inputs/style.json", "{\"synthetic\":true}\n");
            Write("inputs/evidence.json", "{\"synthetic\":true,\"acceptance\":\"fixture only; no actual rights or approval\"}\n");
            WriteJson("inputs/licenses.json", new SplatLicenseEvidence { SchemaVersion = "bubo-splat-license-evidence/v1", Task = TaskId,
                Backend = "triposplat", Accepted = true, LicenseIds = LicenseIds, Evidence = [Identity("inputs/evidence.json")] });
            request = new() { SchemaVersion = "bubo-splat-installation-request/v1", Task = TaskId, ReceiptPath = "state/installation.json",
                ModelDirectory = "tools/models", SourceDirectory = "tools/source", EnvironmentRoot = "tools/environment", PythonBaseRoot = "tools/base",
                WheelhouseRoot = "tools/wheels", PythonArchive = "tools/archive/python.tar.gz", NoticeDirectory = "tools/notices",
                Python = Identity("tools/environment/Scripts/python.exe"), Licenses = [Identity("inputs/licenses.json")], ComponentReview = new() { Path = "inputs/component-review.json", Sha256 = new string('0', 64) } };
            RefreshDescriptors();
        }

        public string Full(string path) => Path.GetFullPath(Path.Combine(Root, path));
        public void Write(string path, string value) => Write(path, Bytes(value));
        public void Write(string path, byte[] value)
        { var full = Full(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllBytes(full, value); }
        void WriteJson(string path, object value) => Write(path, JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), SplatJson.Options));
        public void Flip(string path) { var bytes = File.ReadAllBytes(Full(path)); bytes[0] ^= 1; Write(path, bytes); }
        SplatSource Identity(string path) => new() { Path = path, Sha256 = Workspace.Hash(Full(path)) };
        JsonObject Pin(string path, string name) => new() { ["path"] = name, ["sha256"] = Identity(path).Sha256, ["bytes"] = new FileInfo(Full(path)).Length };
        public void ChangeRequest(Func<SplatInstallationRequest, SplatInstallationRequest> change) { request = change(request); RefreshDescriptors(); }
        public void RefreshDescriptors()
        {
            var specification = new JsonObject { ["backend"] = "triposplat", ["artifacts"] = artifacts.DeepClone() };
            WriteJson("inputs/component-review.json", new SplatComponentReview { SchemaVersion = "bubo-splat-component-review/v1", Task = TaskId, Backend = "triposplat",
                CatalogSha256 = Identity("bundle/resources/image-to-splat/catalog.json").Sha256,
                EnvironmentSpecificationSha256 = Fixtures.Hash(Bytes(specification.ToJsonString())), CommercialUseAllowed = true, NoticesVerified = true,
                ProvenanceDisposition = "accepted-disclosed-derivative-lineage", UnresolvedComponents = ["dinov3-conversion"], Evidence = [Identity("inputs/evidence.json")],
                NoticeFiles = Directory.EnumerateFiles(Full("tools/notices")).Order(StringComparer.Ordinal).Select(path => Identity(Path.GetRelativePath(Root, path).Replace('\\', '/'))).ToArray() });
            request = request with { ComponentReview = Identity("inputs/component-review.json") };
            WriteJson("inputs/request.json", request); SaveContext();
        }
        void SaveContext()
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal) { ["inputs/style.json"] = "policy", ["inputs/evidence.json"] = "rights",
                ["inputs/licenses.json"] = "rights", ["inputs/component-review.json"] = "rights", ["inputs/request.json"] = "input" };
            if (receiptAdmitted) entries["state/installation.json"] = "input";
            WriteJson("context.json", new { schemaVersion = "bubo-host-context/v1", projectRoot = Root, task = TaskId,
                roots = new { state = "state", staging = "staging", tools = new[] { "tools" }, inputs = new[] { "inputs" }, outputs = new[] { "outputs" } },
                policy = new { pipeline = "LOCAL", styleSha256 = Identity("inputs/style.json").Sha256, engine = new { effectiveEngine = "THREEJS" }, libraries = Array.Empty<object>() },
                references = entries.Select(pair => new { path = pair.Key, sha256 = Identity(pair.Key).Sha256, kind = pair.Value }).ToArray(),
                tools = new[] { new { path = Full("tools/environment/Scripts/python.exe"), sha256 = Identity("tools/environment/Scripts/python.exe").Sha256, kind = "python" } },
                budgets = new { timeoutSeconds = 30, maxLogBytes = 1048576, maxPrivateBytes = 268435456 }, permissions = new { provisioning = true, promotion = false } });
        }
        public SplatInstallation Service()
        {
            var host = Bubo.Host.HostExecutionContext.Load(Root, "context.json");
            return new(new Workspace(Root, host), new AuditedResources(Full("bundle"), Workspace.Hash(Full("bundle/resources/catalog.json"))));
        }
        public object Register(CancellationToken cancellation = default) => Service().Execute("inputs/request.json", true, cancellation);
        public void AdmitReceipt() { receiptAdmitted = true; SaveContext(); }
        public PreparedSplatInstallation Verify() => Service().Verify(Identity("state/installation.json"));
        void InstallWheel(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes); using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            { using var input = entry.Open(); using var content = new MemoryStream(); input.CopyTo(content); Write("tools/environment/Lib/site-packages/" + entry.FullName, content.ToArray()); }
        }
        static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
        static byte[] Wheel(string name)
        {
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var pair in new Dictionary<string, string> { [name + "/__init__.py"] = "# SYNTHETIC; never imported\n", [name + "-0.0.dist-info/METADATA"] = "Name: " + name + "\nVersion: 0.0\n", [name + "-0.0.dist-info/RECORD"] = "" })
                { using var target = zip.CreateEntry(pair.Key).Open(); target.Write(Bytes(pair.Value)); }
            return bytes.ToArray();
        }
        static byte[] PythonArchive(Dictionary<string, byte[]> files)
        {
            using var bytes = new MemoryStream();
            using (var gzip = new GZipStream(bytes, CompressionLevel.Fastest, leaveOpen: true))
            using (var tar = new TarWriter(gzip, leaveOpen: true))
                foreach (var pair in files)
                { using var content = new MemoryStream(pair.Value); var entry = new PaxTarEntry(TarEntryType.RegularFile, "python/" + pair.Key) { DataStream = content }; tar.WriteEntry(entry); }
            return bytes.ToArray();
        }
    }
}
