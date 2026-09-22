using System;
using System.IO;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public sealed class ConfigDirPermissionsTests
{
    [Fact]
    public void EnsureConfigDir_restricts_the_config_dir_to_the_owner_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file modes do not apply on Windows (the config dir is protected differently there).
        }

        string dir = Path.Combine(Path.GetTempPath(), "vela-perm-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("VELA_CONFIG_DIR");
        Environment.SetEnvironmentVariable("VELA_CONFIG_DIR", dir);
        try
        {
            UserConfig.EnsureConfigDir();

            UnixFileMode mode = File.GetUnixFileMode(dir);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VELA_CONFIG_DIR", previous);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
