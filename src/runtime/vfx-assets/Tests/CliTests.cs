using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Bubo.Vfx.Tests;

internal static class CliTests
{
    internal static void Run(string cli, string root)
    {
        Fixture Make(string name) => new(Path.Combine(root, "cli-" + name));
        var good = Make("positive");
        foreach (var operation in new[] { "validate", "inspect" })
        {
            var before = good.Snapshot();
            var response = Execute(cli, Arguments(good, operation));
            Check.That(response.ExitCode == 0 && response.Json["status"]?.GetValue<string>() == "succeeded"
                && response.Json["workflow"]?.GetValue<string>() == "vfx-assets" && response.Json["operation"]?.GetValue<string>() == operation
                && response.Json["data"]?["validation"]?["isValid"]?.GetValue<bool>() == true
                && response.Json["data"]?["observedResources"]?.AsArray().Count == 2, "actual CLI " + operation + " returns successful dependency inspection");
            Check.That(InspectionTests.Equal(before, good.Snapshot()), "actual CLI " + operation + " is read-only");
        }
        foreach (var (name, mutate) in new (string, Action<Fixture>)[]
        {
            ("semantic-missing-reference", f => { f.Package["effects"]![0]!["layers"]![0]!["resourceIds"] = new JsonArray("undeclared"); f.SavePackage(); f.RefreshPackageReference(); }),
            ("semantic-particle-budget", f => { f.Package["budgets"]!["maxParticles"] = 1; f.SavePackage(); f.RefreshPackageReference(); })
        })
        {
            var fixture = Make(name); mutate(fixture);
            foreach (var operation in new[] { "validate", "inspect" })
            {
                var before = fixture.Snapshot(); var response = Execute(cli, Arguments(fixture, operation));
                Check.That(response.ExitCode != 0 && response.Json["status"]?.GetValue<string>() == "failed"
                    && response.Json["data"]?["validation"]?["isValid"]?.GetValue<bool>() == false
                    && response.Json["data"]?["validation"]?["issues"]?.AsArray().Count > 0, "actual CLI " + operation + " fails with actionable semantic report: " + name);
                Check.That(InspectionTests.Equal(before, fixture.Snapshot()), "actual CLI semantic failure performs no writes: " + name);
            }
        }
        foreach (var (name, mutate) in new (string, Action<Fixture>)[]
        {
            ("input-reference-transplant", f => { f.Context["references"]![0]!["kind"] = "review"; f.SaveContext(); }),
            ("style-transplant", f => { f.Package["styleSha256"] = new string('0', 64); f.SavePackage(); f.RefreshPackageReference(); }),
            ("engine-transplant", f => { f.Package["engine"]!["definitionSha256"] = new string('0', 64); f.SavePackage(); f.RefreshPackageReference(); }),
            ("changed-resource", f => { File.AppendAllText(f.Full(Fixture.TexturePath), "changed"); })
        })
        {
            var fixture = Make(name); mutate(fixture);
            foreach (var operation in new[] { "validate", "inspect" })
            {
                var before = fixture.Snapshot(); var response = Execute(cli, Arguments(fixture, operation));
                Check.That(response.ExitCode != 0 && response.Json["status"]?.GetValue<string>() == "failed", "actual CLI " + operation + " rejects " + name);
                Check.That(InspectionTests.Equal(before, fixture.Snapshot()), "actual CLI binding/resource rejection is read-only: " + name);
            }
        }
        foreach (var options in new[]
        {
            new[] { "vfx-assets", "render", "--api-version", "2" },
            new[] { "vfx-assets", "process", "--api-version", "2" },
            Arguments(good, "inspect").Concat(new[] { "--unknown-vfx-option", "value" }).ToArray(),
            Arguments(good, "validate").Concat(new[] { "--script", "untrusted.py" }).ToArray(),
            Arguments(good, "inspect").Concat(new[] { "--manifest", Fixture.PackagePath }).ToArray(),
            Arguments(good, "inspect").Concat(new[] { "--task", "vfx-fixture" }).ToArray()
        })
        {
            var before = good.Snapshot(); var response = Execute(cli, options);
            Check.That(response.ExitCode != 0 && response.Json["status"]?.GetValue<string>() == "failed", "actual CLI rejects unavailable operation or undeclared option: " + string.Join(' ', options.Take(2)) + " " + options[^2]);
            Check.That(InspectionTests.Equal(before, good.Snapshot()), "actual CLI unsupported-operation/options fail without writes");
        }
        if (OperatingSystem.IsLinux())
        {
            var fifo = Make("fifo-dependency");
            var texture = fifo.Full(Fixture.TexturePath);
            File.Delete(texture);
            if (MkFifo(texture, 0x180) != 0) // Owned fixture, mode 0600; no reader or writer is opened.
                throw new InvalidOperationException("Cannot create actual Linux FIFO fixture; errno " + Marshal.GetLastPInvokeError());
            Check.That(File.Exists(texture), "actual Linux FIFO satisfies ordinary file-existence admission");
            // Never snapshot/hash/read this fixture: opening the FIFO without a peer would block.
            // Execute owns the existing 15-second process watchdog, so a regression fails boundedly.
            foreach (var operation in new[] { "validate", "inspect" })
            {
                var response = Execute(cli, Arguments(fifo, operation));
                Check.That(response.ExitCode != 0 && response.Json["status"]?.GetValue<string>() == "failed", "actual CLI " + operation + " rejects an existing Linux FIFO dependency without blocking");
                Check.That(File.Exists(texture) && new FileInfo(texture).Length == 0, "actual CLI " + operation + " retains the input FIFO");
                Check.That(!Directory.Exists(fifo.Full(".bubo")) && !Directory.Exists(fifo.Full(".art-staging"))
                    && !Directory.Exists(fifo.Full("assets")) && !Directory.Exists(fifo.Full(".local")), "actual CLI " + operation + " FIFO rejection creates no state, staging, output or tool directories");
            }
        }
        else Check.Limits.Add("Linux-only actual FIFO CLI regression is not run on " + RuntimeInformation.OSDescription + ".");
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    static string[] Arguments(Fixture fixture, string operation) => ["vfx-assets", operation, "--api-version", "2", "--root", fixture.Root, "--host-context", "host-context.json", "--package", Fixture.PackagePath];

    static (int ExitCode, JsonObject Json) Execute(string cli, string[] arguments)
    {
        var start = new ProcessStartInfo(cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : cli)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(cli);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start explicitly selected built CLI");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000)) { process.Kill(true); throw new InvalidOperationException("VFX CLI contract fixture exceeded its 15 second bound"); }
        Task.WaitAll(stdout, stderr);
        return (process.ExitCode, JsonNode.Parse(stdout.Result)?.AsObject() ?? throw new InvalidOperationException("CLI returned no JSON result: " + stderr.Result));
    }
}
