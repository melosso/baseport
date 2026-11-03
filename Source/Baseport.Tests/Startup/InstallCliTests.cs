using Xunit;

namespace Baseport.Tests;

public class InstallCliTests
{
    [Theory]
    [InlineData(null, "http://localhost:5000")]
    [InlineData("ExecStart=/opt/baseport/Baseport\n", "http://localhost:5000")]
    [InlineData("ExecStart=/opt/baseport/Baseport --urls http://0.0.0.0:8080;https://x\n", "http://localhost:8080")]
    [InlineData("ExecStart=/opt/baseport/Baseport --urls=http://[::]:5000\n", "http://localhost:5000")]
    [InlineData("ExecStart=/opt/baseport/Baseport --urls http://*:7000\n", "http://localhost:7000")]
    [InlineData("ExecStart=/opt/baseport/Baseport --urls http://10.0.0.5:9000\n", "http://10.0.0.5:9000")]
    public void ServiceUrlFromUnit(string? unit, string expected) =>
        Assert.Equal(expected, InstallCli.ServiceUrl(unit));

    [Fact]
    public void UnitRoundTripsUrl()
    {
        var unit = InstallCli.UnitFile("/opt/baseport", "--urls http://0.0.0.0:6000");
        Assert.Contains("ExecStart=/opt/baseport/Baseport --urls http://0.0.0.0:6000\n", unit, StringComparison.Ordinal);
        Assert.Contains("User=baseport", unit, StringComparison.Ordinal);
        Assert.Equal("http://localhost:6000", InstallCli.ServiceUrl(unit));
    }

    [Theory]
    [InlineData(new[] { "logs" }, 200)]
    [InlineData(new[] { "logs", "50" }, 50)]
    [InlineData(new[] { "logs", "-5" }, null)]
    [InlineData(new[] { "logs", "ten" }, null)]
    public void LogLineCount(string[] args, int? expected) =>
        Assert.Equal(expected, InstallCli.LineCount(args));

    [Fact]
    public void UninstallKeepsData()
    {
        foreach (var name in new[] { "baseport.db", "baseport.key", ".env", "uploads", "backups", "log", "appsettings.json" })
            Assert.Contains(name, (IReadOnlySet<string>)InstallCli.Kept);
        Assert.DoesNotContain("Baseport", (IReadOnlySet<string>)InstallCli.Kept);
    }
}
