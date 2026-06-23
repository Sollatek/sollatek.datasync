using System.Xml.Linq;

namespace Sollatek.DataSync.Tests;

public sealed class MonitoringProviderPackagingTests
{
    [Fact]
    public void ExecutableProject_DoesNotReferenceOptionalMonitoringPackagesDirectly()
    {
        var project = LoadProject("Sollatek.DataSync", "Sollatek.DataSync.csproj");

        Assert.DoesNotContain(
            project.Descendants("PackageReference"),
            x => IsInclude(x, "Azure.Monitor.OpenTelemetry.Exporter"));
        Assert.DoesNotContain(
            project.Descendants("PackageReference"),
            x => IsInclude(x, "OpenTelemetry.Exporter.OpenTelemetryProtocol"));
        Assert.DoesNotContain(
            project.Descendants("PackageReference"),
            x => IsInclude(x, "OpenTelemetry.Extensions.Hosting"));
        Assert.DoesNotContain(
            project.Descendants("FrameworkReference"),
            x => IsInclude(x, "Microsoft.AspNetCore.App"));
    }

    [Fact]
    public void ExecutableProject_DefaultsMonitoringProviderToNone()
    {
        var project = LoadProject("Sollatek.DataSync", "Sollatek.DataSync.csproj");

        var defaultProperty = project
            .Descendants("DataSyncMonitoringProvider")
            .SingleOrDefault(x => string.Equals(
                (string?)x.Attribute("Condition"),
                "'$(DataSyncMonitoringProvider)' == ''",
                StringComparison.Ordinal));

        Assert.NotNull(defaultProperty);
        Assert.Equal("none", defaultProperty.Value.Trim());
    }

    [Fact]
    public void Dockerfile_FailsStatusMonitoringBuildWhenAspNetRuntimeIsNotSelected()
    {
        var path = Path.Combine(FindRepositoryRoot(), "Sollatek.DataSync", "Dockerfile");
        var dockerfile = File.ReadAllText(path);

        Assert.Contains("DATASYNC_MONITORING_PROVIDER", dockerfile);
        Assert.Contains("DOTNET_RUNTIME_IMAGE", dockerfile);
        Assert.Contains("Status endpoint monitoring requires DOTNET_RUNTIME_IMAGE=aspnet", dockerfile);
    }

    [Theory]
    [InlineData("Stores", "Sollatek.DataSync.Sql", "Sollatek.DataSync.Sql.csproj")]
    [InlineData("Stores", "Sollatek.DataSync.Mongo", "Sollatek.DataSync.Mongo.csproj")]
    [InlineData("Stores", "Sollatek.DataSync.Filesystem", "Sollatek.DataSync.Filesystem.csproj")]
    [InlineData("Stores", "Sollatek.DataSync.AzureBlob", "Sollatek.DataSync.AzureBlob.csproj")]
    [InlineData("Monitoring", "Sollatek.DataSync.Monitoring.OpenTelemetry", "Sollatek.DataSync.Monitoring.OpenTelemetry.csproj")]
    [InlineData("Monitoring", "Sollatek.DataSync.Monitoring.Otlp", "Sollatek.DataSync.Monitoring.Otlp.csproj")]
    [InlineData("Monitoring", "Sollatek.DataSync.Monitoring.AzureMonitor", "Sollatek.DataSync.Monitoring.AzureMonitor.csproj")]
    [InlineData("Monitoring", "Sollatek.DataSync.Monitoring.StatusEndpoint", "Sollatek.DataSync.Monitoring.StatusEndpoint.csproj")]
    public void ProviderProjects_AreGroupedUnderProviderFolders(
        string groupDirectory,
        string projectDirectory,
        string projectFile)
    {
        var path = Path.Combine(FindRepositoryRoot(), groupDirectory, projectDirectory, projectFile);

        Assert.True(File.Exists(path), $"Expected provider project at {path}.");
    }

    [Fact]
    public void Solution_GroupsProviderProjectsInStoresAndMonitoringFolders()
    {
        var solution = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Sollatek.DataSync.sln"));

        Assert.Contains("= \"Stores\", \"Stores\"", solution);
        Assert.Contains("= \"Monitoring\", \"Monitoring\"", solution);
        Assert.Contains("Stores\\Sollatek.DataSync.Sql\\Sollatek.DataSync.Sql.csproj", solution);
        Assert.Contains("Stores\\Sollatek.DataSync.Mongo\\Sollatek.DataSync.Mongo.csproj", solution);
        Assert.Contains("Stores\\Sollatek.DataSync.Filesystem\\Sollatek.DataSync.Filesystem.csproj", solution);
        Assert.Contains("Stores\\Sollatek.DataSync.AzureBlob\\Sollatek.DataSync.AzureBlob.csproj", solution);
        Assert.Contains(
            "Monitoring\\Sollatek.DataSync.Monitoring.Otlp\\Sollatek.DataSync.Monitoring.Otlp.csproj",
            solution);
        Assert.Contains(
            "Monitoring\\Sollatek.DataSync.Monitoring.AzureMonitor\\Sollatek.DataSync.Monitoring.AzureMonitor.csproj",
            solution);
        Assert.Contains(
            "Monitoring\\Sollatek.DataSync.Monitoring.StatusEndpoint\\Sollatek.DataSync.Monitoring.StatusEndpoint.csproj",
            solution);
        Assert.Contains(
            "Monitoring\\Sollatek.DataSync.Monitoring.OpenTelemetry\\Sollatek.DataSync.Monitoring.OpenTelemetry.csproj",
            solution);
    }

    [Theory]
    [InlineData("Monitoring", "Sollatek.DataSync.Monitoring.Otlp", "OpenTelemetry.Exporter.OpenTelemetryProtocol")]
    [InlineData("Monitoring", "Sollatek.DataSync.Monitoring.AzureMonitor", "Azure.Monitor.OpenTelemetry.Exporter")]
    [InlineData("Monitoring", "Sollatek.DataSync.Monitoring.StatusEndpoint", "Microsoft.AspNetCore.App")]
    public void MonitoringProviderProjects_OwnTheirOptionalDependencies(
        string groupDirectory,
        string projectDirectory,
        string dependency)
    {
        var project = LoadProject(
            Path.Combine(groupDirectory, projectDirectory),
            $"{projectDirectory}.csproj");

        var packageFound = project.Descendants("PackageReference").Any(x => IsInclude(x, dependency));
        var frameworkFound = project.Descendants("FrameworkReference").Any(x => IsInclude(x, dependency));

        Assert.True(packageFound || frameworkFound, $"{projectDirectory} should reference {dependency}.");
    }

    private static XDocument LoadProject(string directory, string fileName)
    {
        var path = Path.Combine(FindRepositoryRoot(), directory, fileName);
        return XDocument.Load(path);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sollatek.DataSync.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the DataSync repository root.");
    }

    private static bool IsInclude(XElement element, string value)
    {
        return string.Equals((string?)element.Attribute("Include"), value, StringComparison.Ordinal);
    }
}
