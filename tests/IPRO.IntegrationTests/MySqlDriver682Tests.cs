using System;
using System.IO;
using Xunit;

namespace IPRO.IntegrationTests;

// 682. MySqlConnector, the database driver under Pomelo and Hangfire, came in at 2.3.5, which carries
// GHSA-473q-m89c-ghf8 (fixed in 2.6.1). Directory.Packages.props pins it; this keeps it from slipping back
// when Pomelo or Hangfire is updated and asks for less.
public class MySqlDriver682Tests
{
    [Fact]
    public void The_database_driver_in_use_is_past_the_advisory()
    {
        // The assembly version stays 2.0.0.0 across 2.x (strong name); the file version carries the release.
        var file = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(MySqlConnector.MySqlConnection).Assembly.Location);
        var version = new Version(file.FileMajorPart, file.FileMinorPart, file.FileBuildPart);
        Assert.True(version >= new Version(2, 6, 1), $"MySqlConnector {version} is before 2.6.1 (GHSA-473q-m89c-ghf8)");
    }

    [Fact]
    public void The_pin_is_in_the_central_package_file()
    {
        var props = Read("Directory.Packages.props");
        Assert.Contains("<CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>", props);
        Assert.Contains("<PackageVersion Include=\"MySqlConnector\" Version=\"[2.6.2]\" />", props);
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }
}
