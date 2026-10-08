# Administrator restart regression checks

Run on Windows with .NET 10:

```powershell
dotnet run --project MFAAvalonia.Restart.Tests/MFAAvalonia.Restart.Tests.csproj -c Release
```

The executable links the production `AppRuntime` and `RestartHandoff` sources. Separate probe processes use real named mutexes to reproduce the old launch order, verify waiting for the predecessor, preserve ordinary duplicate blocking, and check timeout, fast exit and invalid parent identities. Only the UI dispatcher and logger are replaced; no GUI, UAC prompt or game task is started.

The desktop entry point must call `WaitForParentExit` before `AppRuntime.Initialize`. Releasing the predecessor's mutex before asking for UAC would leave the original app unprotected if elevation is cancelled, so the parent retains its guard until shutdown. The replacement's wait is bounded and does not invoke unresponsive-instance recovery when it times out.

These checks do not verify a human's UAC confirmation or the full GUI restart. The Windows executable's embedded manifest must also be inspected after publishing to confirm `requireAdministrator`.
