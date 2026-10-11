using Semver;
using System;
using System.Numerics;
using System.Text.RegularExpressions;

namespace MFAAvalonia.Helper;

internal static class ReleaseVersion
{
    // Normalize only for comparison. GitHub requests must retain the original tag.
    // Korean resource releases use kr-v; Korean UI releases use ko-v.
    public static SemVersion Parse(string version)
    {
        const string pattern = @"^(?:(?:kr-|ko-)?v)?(?<major>\d+)(\.(?<minor>\d+))?(\.(?<patch>\d+))?(?:-(?<prerelease>[0-9a-zA-Z\-\.]+))?(?:\+(?<build>[0-9a-zA-Z\-\.]+))?$";
        var match = Regex.Match(version.Trim(), pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new FormatException($"Invalid release version: {version}");

        var major = int.Parse(match.Groups["major"].Value);
        var minor = match.Groups["minor"].Success ? int.Parse(match.Groups["minor"].Value) : 0;
        var patch = match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0;
        var prerelease = match.Groups["prerelease"].Success
            ? match.Groups["prerelease"].Value.Split('.')
            : null;
        var build = match.Groups["build"].Success
            ? match.Groups["build"].Value.Split('.')
            : null;

        return new SemVersion(new BigInteger(major), new BigInteger(minor), new BigInteger(patch), prerelease, build);
    }
}
