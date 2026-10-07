using System.Diagnostics;

namespace InsightFlow.IntegrationTests;

/// <summary>
/// Integration tests need a container runtime. Rather than failing on machines without one,
/// tests call <see cref="SkipIfUnavailable"/> and are reported as skipped.
/// </summary>
internal static class DockerAvailability
{
    private static readonly Lazy<bool> IsAvailableLazy = new(Probe);

    public static void SkipIfUnavailable() =>
        Assert.SkipUnless(IsAvailableLazy.Value, "Docker is not running; start Docker Desktop to run integration tests.");

    private static bool Probe()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return false;
            }

            return process.WaitForExit(TimeSpan.FromSeconds(15)) && process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
