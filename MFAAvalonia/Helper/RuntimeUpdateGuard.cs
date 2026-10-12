using Semver;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MFAAvalonia.Helper;

internal static class RuntimeUpdateGuard
{
    // Read version literals, never execute a DLL from an update archive.
    private static string ReadVersion(string path)
    {
        var versions = Regex.Matches(Encoding.ASCII.GetString(File.ReadAllBytes(path)),
                @"v\d+\.\d+\.\d+(?:-(?:alpha|beta|rc)\.\d+)?")
            .Select(match => match.Value).Distinct().ToArray();
        if (versions.Length != 1)
            throw new InvalidDataException($"Cannot identify MaaFramework version: {path}");
        return versions[0];
    }

    public static string GetRejectionReason(string packageRoot, string installedRoot, string arch)
    {
        var paths = new[]
        {
            Path.Combine("runtimes", arch, "native", "MaaFramework.dll"),
            Path.Combine("python", "Lib", "site-packages", "maa", "bin", "MaaFramework.dll")
        };
        if (!paths.Any(relative => File.Exists(Path.Combine(packageRoot, relative))))
            return string.Empty; // A resource-only update does not replace either runtime.
        try
        {
            var effective = new string?[2];
            for (var i = 0; i < paths.Length; i++)
            {
                var incoming = Path.Combine(packageRoot, paths[i]);
                var installed = Path.Combine(installedRoot, paths[i]);
                var currentVersion = File.Exists(installed) ? ReadVersion(installed) : null;
                var packageVersion = File.Exists(incoming) ? ReadVersion(incoming) : null;
                if (currentVersion != null && packageVersion != null &&
                    SemVersion.CompareSortOrder(ReleaseVersion.Parse(packageVersion), ReleaseVersion.Parse(currentVersion)) < 0)
                    return $"MaaFramework downgrade blocked: installed {currentVersion}, package {packageVersion}.";
                effective[i] = packageVersion ?? currentVersion;
            }
            if (effective[0] != null && effective[1] != null && effective[0] != effective[1])
                return $"MaaFramework version mismatch blocked: GUI {effective[0]}, Python {effective[1]}.";
            return string.Empty;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            return $"Cannot verify the update's MaaFramework libraries: {ex.Message}";
        }
    }
}
