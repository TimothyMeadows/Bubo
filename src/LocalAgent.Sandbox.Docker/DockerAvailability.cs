namespace Bubo.LocalAgent.Sandbox.Docker;

public static class DockerAvailability
{
    private static readonly string[] WindowsFallbackPaths =
    {
        @"C:\Program Files\Docker\Docker\resources\bin\docker.exe",
        @"C:\Program Files\Docker\Docker\resources\docker.exe"
    };

    public static bool IsDockerLikelyAvailable(string dockerExecutable = "docker")
    {
        return TryResolveDockerExecutable(dockerExecutable, out _);
    }

    public static bool TryResolveDockerExecutable(
        string dockerExecutable,
        out string resolvedPath)
    {
        var pathEnvironment = Environment.GetEnvironmentVariable("PATH");
        var executableNames = OperatingSystem.IsWindows()
            ? new[] { $"{dockerExecutable}.exe", dockerExecutable }
            : new[] { dockerExecutable };

        if (!string.IsNullOrWhiteSpace(pathEnvironment))
        {
            foreach (var directory in pathEnvironment.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                foreach (var executableName in executableNames)
                {
                    var candidate = Path.Combine(directory, executableName);
                    if (File.Exists(candidate))
                    {
                        resolvedPath = candidate;
                        return true;
                    }
                }
            }
        }

        if (OperatingSystem.IsWindows() &&
            string.Equals(dockerExecutable, "docker", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var candidate in WindowsFallbackPaths)
            {
                if (File.Exists(candidate))
                {
                    resolvedPath = candidate;
                    return true;
                }
            }
        }

        resolvedPath = string.Empty;
        return false;
    }
}
