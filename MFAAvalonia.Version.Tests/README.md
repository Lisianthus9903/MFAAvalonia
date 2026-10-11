# Release version regression checks

Run with .NET 10:

```sh
dotnet run --project MFAAvalonia.Version.Tests/MFAAvalonia.Version.Tests.csproj -c Release
```

The console checks link the production `ReleaseVersion` parser and use the same
Semver 3.0.0 dependency as the client. They cover `kr-v` resource tags, `ko-v` UI
tags, ordinary versions, prerelease ordering, build metadata, whitespace and
invalid input without launching the GUI, downloading updates or changing files.

Tag prefixes are removed only for comparison. GitHub release and asset requests
continue to use the complete tag, such as `kr-v0.9.5-kr.20261011`.

Normal SemVer ordering is retained: `0.9.5-kr.20261011` is lower than
`0.9.5-kr.fix2.20261009` because a numeric prerelease identifier sorts before a
text identifier. A later release intended to supersede both must use a higher
version (for example `kr-v0.9.6`), rather than relying on the date in the suffix.

Existing client binaries require a rebuilt client containing this fix; changing
only the server's release tag does not patch an already installed parser.
