using MFAAvalonia.Helper;

var failures = new List<string>();
var checks = 0;

void EqualPrecedence(string left, string right) => Compare(left, right, 0);

void Compare(string left, string right, int expected)
{
    checks++;
    try
    {
        var actual = Math.Sign(ReleaseVersion.Parse(left).ComparePrecedenceTo(ReleaseVersion.Parse(right)));
        if (actual != expected)
            failures.Add($"{left} vs {right}: expected {expected}, got {actual}");
    }
    catch (Exception error)
    {
        failures.Add($"{left} vs {right}: {error.Message}");
    }
}

foreach (var prefix in new[] { "", "v", "V", "kr-v", "KR-V", "ko-v", "KO-V" })
{
    EqualPrecedence($"{prefix}1.2.3", "v1.2.3");
    EqualPrecedence($"  {prefix}1.2.3  ", "1.2.3");
}

EqualPrecedence("kr-v0.9.5-kr.20261011", "v0.9.5-kr.20261011");
EqualPrecedence("ko-v2.16.3-kr.2", "v2.16.3-kr.2");
EqualPrecedence("1", "1.0.0");
EqualPrecedence("v1.2", "1.2.0");
EqualPrecedence("kr-v1.2.3+build.1", "v1.2.3+build.2");
Compare("kr-v0.9.5-kr.20261011", "v0.9.5-kr.20261010", 1);
Compare("kr-v0.9.5-kr.20261010", "v0.9.5-kr.20261011", -1);
Compare("ko-v2.16.3-kr.2", "v2.16.3-kr.1", 1);
EqualPrecedence("ko-v2.16.3-kr.3", "v2.16.3-kr.3");
Compare("ko-v2.16.3-kr.3", "v2.16.3-kr.2", 1);
Compare("kr-v0.9.5-kr.20261012", "v0.9.5-kr.20261011", 1);
Compare("kr-v0.9.6", "v0.9.5-kr.fix2.20261009", 1);
Compare("v1.10.0", "v1.9.9", 1);
Compare("v1.2.3-beta.10", "v1.2.3-beta.2", 1);
Compare("kr-v1.2.3", "v1.2.3-rc.1", 1);
Compare("v1.2.3-alpha.1", "v1.2.3-beta.1", -1);
// Preserve SemVer precedence; do not reinterpret custom prerelease names as dates.
Compare("kr-v0.9.5-kr.20261011", "v0.9.5-kr.fix2.20261009", -1);

foreach (var invalid in new[] { "", " ", "kr-v", "kr-1.2.3", "ko-1.2.3", "release-v1.2.3", "kr-ko-v1.2.3", "v1.2.3junk", "v1.2.3.4", "v1.2.3-", "v1.2.3-beta..1" })
{
    checks++;
    try
    {
        ReleaseVersion.Parse(invalid);
        failures.Add($"Invalid version accepted: '{invalid}'");
    }
    catch (Exception error) when (error is FormatException or ArgumentException)
    {
        // Invalid versions must not silently become 0.0.0.
    }
}

foreach (var failure in failures)
    Console.Error.WriteLine($"FAIL: {failure}");
Console.WriteLine($"{checks - failures.Count}/{checks} release version checks passed.");
return failures.Count == 0 ? 0 : 1;
