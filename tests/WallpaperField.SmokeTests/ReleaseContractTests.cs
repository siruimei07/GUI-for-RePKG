using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WallpaperField;

internal static class ReleaseContractTests
{
    private const string ProductVersion = "1.2.1";
    private const string FileVersion = "1.2.1.0";

    internal static void Run(Action<bool, string> assert)
    {
        var failures = new List<string>();
        var assembly = typeof(App).Assembly;
        var assemblyVersion = assembly.GetName().Version;
        if (assemblyVersion != new Version(1, 0, 0, 0))
        {
            failures.Add($"AssemblyVersion was {assemblyVersion}, expected 1.0.0.0");
        }

        var fileInfo = FileVersionInfo.GetVersionInfo(assembly.Location);
        if (!string.Equals(fileInfo.FileVersion, FileVersion, StringComparison.Ordinal))
        {
            failures.Add($"FileVersion was {fileInfo.FileVersion}, expected {FileVersion}");
        }

        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (informationalVersion is null
            || !Regex.IsMatch(
                informationalVersion,
                $"^{Regex.Escape(ProductVersion)}\\+[0-9a-f]{{40}}$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
        {
            failures.Add(
                $"InformationalVersion was {informationalVersion ?? "<missing>"}, "
                + $"expected {ProductVersion}+<40-hex-commit>");
        }

        if (!string.Equals(
                fileInfo.ProductVersion,
                informationalVersion,
                StringComparison.Ordinal))
        {
            failures.Add(
                $"PE ProductVersion '{fileInfo.ProductVersion}' did not match "
                + $"InformationalVersion '{informationalVersion}'");
        }

        var manifestPath = Path.Combine(FindRepositoryRoot(), "app.manifest");
        var manifest = XDocument.Load(manifestPath, LoadOptions.PreserveWhitespace);
        XNamespace assemblyNamespace = "urn:schemas-microsoft-com:asm.v1";
        var identity = manifest.Root?.Element(assemblyNamespace + "assemblyIdentity");
        var manifestVersion = (string?)identity?.Attribute("version");
        if (!string.Equals(manifestVersion, FileVersion, StringComparison.Ordinal))
        {
            failures.Add(
                $"Application manifest version was {manifestVersion ?? "<missing>"}, "
                + $"expected {FileVersion}");
        }

        assert(
            failures.Count == 0,
            "Release version contract failures: " + string.Join("; ", failures));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WallpaperField.csproj")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Wallpaper Field repository root.");
    }
}
