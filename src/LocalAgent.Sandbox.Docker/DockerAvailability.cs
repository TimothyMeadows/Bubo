namespace Bubo.LocalAgent.Sandbox.Docker;

public static class DockerAvailability
{
    public static bool IsDockerLikelyAvailable(string dockerExecutable = "docker")
    {
        var pathEnvironment = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnvironment))
        {
            return false;
        }

        var executableNames = OperatingSystem.IsWindows()
            ? new[] { dockerExecutable, $"{dockerExecutable}.exe" }
            : new[] { dockerExecutable };

        foreach (var directory in pathEnvironment.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var executableName in executableNames)
            {
                if (File.Exists(Path.Combine(directory, executableName)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
