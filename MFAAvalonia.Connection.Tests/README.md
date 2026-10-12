# Windows connection regression checks

Run with .NET 10 and the installation's matching Python binding:

```powershell
$env:MFA_TEST_AGENT_PYTHON='D:\AI_Workspaces\Claude\MaaStellaSora_KR\python\python.exe'
dotnet run --project MFAAvalonia.Connection.Tests/MFAAvalonia.Connection.Tests.csproj -c Release -- connection-results.json
```

The executable exercises the production Win32 recovery, awaited stop/restart, Agent reuse/cleanup and duplicate-launch checks. It creates an off-screen test window and a real Python AgentServer; it does not select a game window or send game input. Confirm `nativeVersion`, `fixtureOnly` and `agentServerChecks` in the report.

MaaFramework 5.14.3 no longer reproduces the previous idle-preview IPC response buildup. The test now requires a healthy link after 10,000 idle screenshots, then deliberately disconnects the client while the fixture process stays alive. It verifies that the existing recovery guard still rejects and cleans up that session. Process death and tasker/controller/resource cleanup remain separate checks.
