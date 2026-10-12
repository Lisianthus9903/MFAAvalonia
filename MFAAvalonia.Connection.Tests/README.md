# Windows connection regression checks

Run with .NET 10 and the installation's matching Python binding:

```powershell
$env:MFA_TEST_AGENT_PYTHON='D:\AI_Workspaces\Claude\MaaStellaSora_KR\python\python.exe'
$env:MFA_TEST_CURRENT_RUNTIME='C:\fixtures\5.14.3\MaaFramework.dll'
$env:MFA_TEST_OLDER_RUNTIME='C:\fixtures\5.12.3\MaaFramework.dll'
dotnet run --project MFAAvalonia.Connection.Tests/MFAAvalonia.Connection.Tests.csproj -c Release -- connection-results.json
```

The executable exercises the production Win32 recovery, awaited stop/restart, Agent reuse/cleanup and duplicate-launch checks. It creates an off-screen test window and a real Python AgentServer; it does not select a game window or send game input. Confirm `nativeVersion`, `fixtureOnly` and `agentServerChecks` in the report.

AnchoredTouch checks cover the mouse-only dropdown, its displayed name, canonical/numeric configuration roundtrips, the production configuration reader, interface parsing and actual native controller creation through the stable C# binding. No touch or click is injected by these checks.

The runtime fixtures must be real DLLs from the indicated official versions. Update-package checks read their version literals without executing archive DLLs. They reject older full packages, mixed GUI/Python versions and unidentifiable runtimes while accepting resource-only, coherent same-version and coherent newer packages. Saved instance input methods take precedence over interface defaults. A separate GUI smoke check should verify that selecting AnchoredTouch, closing and reopening preserves the selection and keyboard method.

MaaFramework 5.14.3 no longer reproduces the previous idle-preview IPC response buildup. The test now requires a healthy link after 10,000 idle screenshots, then deliberately disconnects the client while the fixture process stays alive. It verifies that the existing recovery guard still rejects and cleans up that session. Process death and tasker/controller/resource cleanup remain separate checks.
