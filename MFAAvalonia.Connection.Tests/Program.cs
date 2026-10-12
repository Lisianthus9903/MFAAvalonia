using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using MaaFramework.Binding;
using MFAAvalonia;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Configuration;
using MFAAvalonia.Helper.Converters;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.ViewModels.UsersControls.Settings;
using JsonConvert = Newtonsoft.Json.JsonConvert;

internal static class Program
{
    private static readonly List<string> Passed = [];
    private delegate Task<bool> Recover(Func<bool> valid, Action invalidate,
        Func<CancellationToken, Task> discover, bool allowDiscovery, CancellationToken token);

    private static int Main(string[] args)
    {
        PrivatePathHelper.SetupNativeLibraryResolver();
        var helper = typeof(MaaProcessor).Assembly.GetType("MFAAvalonia.Helper.Win32TargetRecovery", true)!;
        var recover = helper.GetMethod("EnsureReadyAsync")!.CreateDelegate<Recover>();
        var stopMethod = typeof(MaaProcessor).GetMethod("ExecuteStopCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check(stopMethod.ReturnType == typeof(Task), "stop completion is awaitable by the command loop");
        var processor = RuntimeHelpers.GetUninitializedObject(typeof(MaaProcessor));
        var stop = stopMethod.CreateDelegate<Func<bool, Func<Task>, Task>>(processor);

        var title = "MFA-Connection-Tests-" + Guid.NewGuid().ToString("N");
        var hwnd = CreateFixture(title);
        DesktopWindowInfo? selected = null;
        var invalidations = 0;
        var discoveries = 0;
        bool Valid() => selected != null && MaaProcessor.Toolkit.Desktop.Window.Find().Any(w =>
            w.Handle == selected.Handle && w.Name == selected.Name && w.ClassName == selected.ClassName);
        void Invalidate() { selected = null; invalidations++; }
        Task Discover(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            discoveries++;
            selected = MaaProcessor.Toolkit.Desktop.Window.Find().FirstOrDefault(w =>
                w.Name.StartsWith(title, StringComparison.Ordinal)
                && w.ClassName.Equals("Static", StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }
        bool Ready(bool allow = true) => recover(Valid, Invalidate, Discover, allow, default).GetAwaiter().GetResult();

        try
        {
            RunInputMethodChecks(hwnd);
            RunRuntimePackageChecks();
            try
            {
                using var invalid = CreateController(IntPtr.Zero);
                throw new Exception("empty native handle unexpectedly accepted");
            }
            catch (ArgumentException ex)
            {
                Check(ex.ParamName == "info.Handle", "empty handle reproduces the reported native constructor failure");
            }

            Check(Ready() && selected?.Handle == hwnd, "a null selection rediscovers the actual test window");
            using (var controller = CreateController(selected!.Handle))
                Check(controller != null, "the recovered handle creates a native controller without game input");

            var previousDiscoveries = discoveries;
            var previousInvalidations = invalidations;
            Check(Ready() && discoveries == previousDiscoveries && invalidations == previousInvalidations,
                "a valid selection keeps its controller and skips rediscovery");

            var oldSelection = selected;
            DestroyFixture(ref hwnd);
            hwnd = CreateFixture(title + "-recreated");
            Check(!Valid() && oldSelection != null, "a closed or recreated window invalidates its previous identity");
            Check(Ready() && selected?.Handle == hwnd && selected.Name.EndsWith("-recreated"),
                "an expired selection is replaced by the recreated matching window");

            DestroyFixture(ref hwnd);
            Check(!Ready() && selected == null, "a missing window stays disconnected without selecting another application");
            hwnd = CreateFixture(title + "-returned");
            Check(!Ready(false) && selected == null, "disabled automatic discovery is respected for a null selection");
            Check(Ready(), "a later retry succeeds after the target returns");

            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                var before = invalidations;
                ExpectCanceled(recover(Valid, Invalidate, Discover, true, canceled.Token));
                Check(invalidations == before && Valid(), "an already canceled run does not clear a working selection");
            }

            selected = null;
            using (var canceled = new CancellationTokenSource())
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var pending = recover(Valid, Invalidate, async token =>
                {
                    entered.SetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }, true, canceled.Token);
                entered.Task.Wait(TimeSpan.FromSeconds(3));
                canceled.Cancel();
                ExpectCanceled(pending);
                Check(selected == null && Ready(), "canceling discovery does not poison the next start token");
            }

            for (var i = 0; i < 20; i++)
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var stopDone = false;
                var started = false;
                var command = Task.Run(async () =>
                {
                    await stop(true, async () =>
                    {
                        entered.SetResult();
                        await release.Task;
                        stopDone = true;
                    });
                    if (!stopDone) throw new Exception("start overtook stop completion");
                    started = true;
                });
                if (!entered.Task.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("stop entry");
                Check(!command.IsCompleted && !started, "restart waits for actual stop completion " + i);
                release.SetResult();
                if (!command.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("stop completion");
                Check(stopDone && started && Ready(), "stop and restart finish with a usable window " + i);
            }

            var agentServerChecks = RunAgentChecks(processor, hwnd);
            RunDuplicateLaunchChecks();

            var report = new { passed = Passed.Count, checks = Passed,
                nativeVersion = NativeBindingContext.LibraryVersion, gameInput = false,
                fixtureOnly = true, agentServerChecks, source = typeof(MaaProcessor).Assembly.Location };
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            if (args.Length > 0) File.WriteAllText(args[0], json);
            Console.WriteLine(json);
            return 0;
        }
        finally
        {
            DestroyFixture(ref hwnd);
        }
    }

    private static void RunRuntimePackageChecks()
    {
        var currentDll = Environment.GetEnvironmentVariable("MFA_TEST_CURRENT_RUNTIME");
        var olderDll = Environment.GetEnvironmentVariable("MFA_TEST_OLDER_RUNTIME");
        if (string.IsNullOrEmpty(currentDll) || string.IsNullOrEmpty(olderDll))
            throw new InvalidOperationException("Set MFA_TEST_CURRENT_RUNTIME and MFA_TEST_OLDER_RUNTIME to real MaaFramework DLLs.");
        var guard = typeof(MaaProcessor).Assembly.GetType("MFAAvalonia.Helper.RuntimeUpdateGuard", true)!;
        var reject = guard.GetMethod("GetRejectionReason")!.CreateDelegate<Func<string, string, string, string>>();
        var root = Path.Combine(Path.GetTempPath(), "mfa-runtime-guard-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(root, "installed");
        var package = Path.Combine(root, "package");
        var gui = Path.Combine("runtimes", "win-x64", "native", "MaaFramework.dll");
        var python = Path.Combine("python", "Lib", "site-packages", "maa", "bin", "MaaFramework.dll");
        void Put(string destination, string relative, string source)
        {
            var path = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(source, path, true);
        }
        try
        {
            Directory.CreateDirectory(package);
            Put(installed, gui, currentDll);
            Put(installed, python, currentDll);
            Check(reject(package, installed, "win-x64") == "", "resource-only packages remain accepted");
            Put(package, gui, olderDll);
            Put(package, python, olderDll);
            Check(reject(package, installed, "win-x64").Contains("downgrade"), "a real older full package is rejected before file replacement");
            Put(package, gui, currentDll);
            Put(package, python, currentDll);
            Check(reject(package, installed, "win-x64") == "", "a coherent same-version package remains accepted");
            Put(installed, gui, olderDll);
            Put(installed, python, olderDll);
            Check(reject(package, installed, "win-x64") == "", "a coherent newer GUI and Python pair remains accepted");
            File.Delete(Path.Combine(package, python));
            Check(reject(package, installed, "win-x64").Contains("mismatch"), "a partial upgrade cannot mix GUI and Python native versions");
            File.WriteAllText(Path.Combine(package, gui), "invalid runtime fixture");
            Check(reject(package, installed, "win-x64").Contains("Cannot verify"), "an unidentifiable incoming runtime is rejected without execution");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void RunInputMethodChecks(IntPtr hwnd)
    {
        var converter = new Win32InputMethodConverter();
        var anchored = Win32InputMethodConverter.AnchoredTouch;
        Check((ulong)anchored == 1024, "AnchoredTouch uses the MaaDef.h v5.14.3 UInt64 ABI value");
        Check(ConnectSettingsUserControlModel.Win32ControlMouseInputTypes.Contains(anchored),
            "the mouse input dropdown includes AnchoredTouch");
        Check(!ConnectSettingsUserControlModel.Win32ControlInputTypes.Contains(anchored),
            "the keyboard input dropdown excludes mouse-only AnchoredTouch");
        Check(ConnectSettingsUserControlModel.Win32ControlMouseInputTypes.Count ==
              ConnectSettingsUserControlModel.Win32ControlInputTypes.Count + 1,
            "all existing mouse and keyboard choices are retained");
        Check((string)converter.Convert(anchored, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture)
              == "AnchoredTouch", "the dropdown displays AnchoredTouch instead of 1024");
        Check(JsonConvert.SerializeObject(anchored, converter) == "\"AnchoredTouch\"",
            "AnchoredTouch is saved by its canonical name");
        foreach (var json in new[] { "\"AnchoredTouch\"", "\"anchoredtouch\"", "\"1024\"", "1024" })
            Check(JsonConvert.DeserializeObject<Win32InputMethod>(json, converter) == anchored,
                "AnchoredTouch loads from " + json);
        foreach (var method in ConnectSettingsUserControlModel.Win32ControlInputTypes)
            Check(JsonConvert.DeserializeObject<Win32InputMethod>(JsonConvert.SerializeObject(method, converter), converter) == method,
                "existing input setting roundtrips: " + method);

        var stored = JsonConvert.DeserializeObject<Dictionary<string, object>>(
            "{\"Win32ControlMouseType\":\"AnchoredTouch\",\"Win32ControlKeyboardType\":\"SendMessageWithCursorPos\"}")!;
        var config = new MFAConfiguration("Input regression", "input-regression", stored);
        Check(config.GetValue(ConfigurationKeys.Win32ControlMouseType, Win32InputMethod.SendMessage,
                  Win32InputMethod.None, converter) == anchored,
            "the production configuration reader restores the saved mouse method");
        Check(config.GetValue(ConfigurationKeys.Win32ControlKeyboardType, Win32InputMethod.SendMessage,
                  Win32InputMethod.None, converter) == Win32InputMethod.SendMessageWithCursorPos,
            "restoring AnchoredTouch leaves the existing keyboard method intact");
        var instance = (InstanceConfiguration)RuntimeHelpers.GetUninitializedObject(typeof(InstanceConfiguration));
        typeof(InstanceConfiguration).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, stored);
        Check(instance.GetValue(ConfigurationKeys.Win32ControlMouseType, Win32InputMethod.SendMessageWithCursorPos,
                  Win32InputMethod.None, converter) == anchored,
            "saved instance mouse input takes precedence over the interface default");
        Check(instance.GetValue(ConfigurationKeys.Win32ControlKeyboardType, Win32InputMethod.SendMessage,
                  Win32InputMethod.None, converter) == Win32InputMethod.SendMessageWithCursorPos,
            "saved instance keyboard input takes precedence over the interface default");
        var parse = typeof(TaskQueueViewModel).GetMethod("ParseWin32InputMethod", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var value in new object[] { "AnchoredTouch", 1024L })
            Check((Win32InputMethod)parse.Invoke(null, [value])! == anchored,
                "interface input parsing accepts AnchoredTouch: " + value);
        using var controller = new MaaWin32Controller(hwnd, Win32ScreencapMethods.GDI,
            anchored, Win32InputMethod.SendMessage, (LinkOption)0, (CheckStatusOption)0);
        Check(!controller.IsInvalid, "the stable C# binding creates a real native AnchoredTouch controller without input");
    }

    // A prescript pointing at the game executable must not start a second game instance
    // ("multiple game instances" error) when the game is already running.
    private static void RunDuplicateLaunchChecks()
    {
        var runner = typeof(ScriptRunner);
        var isRunning = runner.GetMethod("IsExecutableRunning", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<string, bool>>();
        var self = Environment.ProcessPath!;
        Check(isRunning(self), "the running executable is detected by its full path");
        Check(!isRunning(Path.Combine(Path.GetTempPath(), "mfa-not-running-" + Guid.NewGuid().ToString("N") + ".exe")),
            "an executable that is not running is not reported as running");

        var split = runner.GetMethod("SplitScriptPath", BindingFlags.Static | BindingFlags.NonPublic)!;
        var parts = new object?[] { "\"C:\\Games\\My Game\\Game.exe\" -minimized", null, null };
        split.Invoke(null, parts);
        Check((string)parts[1]! == "C:\\Games\\My Game\\Game.exe" && ((string)parts[2]!).Trim() == "-minimized",
            "a quoted script path is split into file and arguments");
    }

    // Agent session reuse after a manual stop. The production decision is driven by a real
    // MaaAgentClient linked to a real Python AgentServer process (agent/main.py's start-up
    // sequence) and a Win32 controller on the off-screen fixture. No game input or GUI.
    private const string AgentScript = """
        import sys
        import threading
        from maa.agent.agent_server import AgentServer
        AgentServer.start_up(sys.argv[-1])
        AgentServer.join()
        AgentServer.shut_down()
        # Fixture only: keep the process alive after disconnecting, to verify that
        # the production reuse guard checks the IPC link as well as process exit.
        threading.Event().wait()
        """;

    private static bool RunAgentChecks(object processor, IntPtr hwnd)
    {
        var configs = new List<MaaInterface.MaaInterfaceAgent> { new() { ChildExec = "python" } };
        var empty = new List<AgentContext>();
        Check(AgentHelper.IsAgentSessionReusable(null, false, empty, out _), "no Agent configuration never forces a restart");
        Check(!AgentHelper.IsAgentSessionReusable(configs, false, empty, out _), "an Agent that was never started is not reused");
        Check(!AgentHelper.IsAgentSessionReusable(configs, true, empty, out _), "a started flag without Agent contexts is not reused");
        Check(!AgentHelper.IsAgentSessionReusable(configs, true, [new AgentContext()], out _), "a released AgentClient is not reused");

        var python = Environment.GetEnvironmentVariable("MFA_TEST_AGENT_PYTHON")
                     ?? @"D:\AI_Workspaces\Claude\MaaStellaSora_KR\python\python.exe";
        if (!File.Exists(python))
        {
            Console.Error.WriteLine($"Agent server checks skipped: {python} not found (set MFA_TEST_AGENT_PYTHON).");
            return false;
        }

        var dir = Directory.CreateTempSubdirectory("mfa-agent-tests-").FullName;
        var script = Path.Combine(dir, "agent_probe.py");
        File.WriteAllText(script, AgentScript);
        var processorType = typeof(MaaProcessor);
        var interfaceProperty = processorType.GetProperty("Interface", BindingFlags.Static | BindingFlags.Public)!;
        var previousInterface = interfaceProperty.GetValue(null);
        var contextsField = processorType.GetField("_agentContexts", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var startedField = processorType.GetField("_agentStarted", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var sessionAlive = processorType.GetMethod("IsAgentSessionAlive", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<bool>>(processor);
        var controller = CreateController(hwnd);
        var resource = new MaaResource();
        var tasker = new MaaTasker
        {
            Controller = controller,
            Resource = resource,
            DisposeOptions = DisposeOptions.All,
        };
        tasker.Controller.LinkStart().Wait();
        var contexts = new List<AgentContext>();
        try
        {
            interfaceProperty.SetValue(null, new MaaInterface { Agent = configs });
            contextsField.SetValue(processor, contexts);
            startedField.SetValue(processor, true);

            // 1. Normal consecutive runs keep reusing a healthy Agent.
            contexts.Add(StartAgent(tasker, python, script, dir));
            Check(sessionAlive() && sessionAlive(), "a live Agent process with a connected client is reused across starts");

            // 2. MaaFramework 5.14.3 fixes the idle screencap response buildup. Keep this
            //    regression as a positive check instead of requiring the old native bug.
            var agentProcess = contexts[0].Process!;
            var screencaps = 0;
            while (contexts[0].Client!.IsAlive && screencaps < 10000)
            {
                tasker.Controller.Screencap().Wait();
                screencaps++;
            }
            Console.Error.WriteLine($"idle screencaps with a healthy Agent link: {screencaps}");
            Check(screencaps == 10000 && !agentProcess.HasExited && sessionAlive(),
                "10000 idle screencaps preserve the healthy Agent link on MaaFramework 5.14.3");
            Check(contexts[0].Client!.LinkStop() && !agentProcess.HasExited && !sessionAlive(),
                "a disconnected Agent link forces a restart even while its process still runs");
            // KillAllAgents disposes the context's Process object, so observe exit through a separate handle.
            using var observer = System.Diagnostics.Process.GetProcessById(agentProcess.Id);
            Check(BoundedCleanup(contexts) && observer.WaitForExit(5000),
                "cleanup of a disconnected live Agent terminates its process without hanging");

            // 3. The Agent process itself died while the controller stayed connected.
            contexts.Add(StartAgent(tasker, python, script, dir));
            Check(sessionAlive(), "a newly started Agent is reused again");
            agentProcess = contexts[0].Process!;
            agentProcess.Kill(true);
            agentProcess.WaitForExit(5000);
            Check(!AgentHelper.IsAgentSessionReusable(configs, true, contexts, out var reason) && reason.Contains("退出"),
                "a dead Agent process forces a restart and names the exited process");
            Check(!sessionAlive(), "the processor rejects the dead Agent session");
            // SetTasker() releases the Agents together with the old tasker. The tasker release must
            // reach its controller and resource instead of failing on an already disposed Process.
            Check(BoundedCleanup(contexts, tasker) && tasker.IsInvalid && controller.IsInvalid && resource.IsInvalid,
                "releasing a dead Agent with its tasker also releases the controller and resource");
            return true;
        }
        finally
        {
            AgentHelper.KillAllAgents(contexts, tasker.IsInvalid ? null : tasker);
            interfaceProperty.SetValue(null, previousInterface);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // Production uses the interface's timeout -1 (no SetTimeout), so a blocking LinkStop would hang forever.
    private static bool BoundedCleanup(List<AgentContext> contexts, MaaTasker? tasker = null)
    {
        var cleanup = Task.Run(() => AgentHelper.KillAllAgents(contexts, tasker));
        return cleanup.Wait(TimeSpan.FromSeconds(15)) && cleanup.Result && contexts.Count == 0;
    }

    private static AgentContext StartAgent(MaaTasker tasker, string python, string script, string dir)
    {
        var ctx = new AgentContext { Client = MaaAgentClient.Create("mfa-test-" + Guid.NewGuid().ToString("N")[..8], tasker) };
        var linked = ctx.Client.LinkStart((id, _) =>
        {
            ctx.Process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = python,
                Arguments = $"-I -u \"{script}\" {id}",
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return ctx.Process;
        }, CancellationToken.None);
        if (!linked) throw new Exception("FAIL: Python AgentServer did not link");
        return ctx;
    }

    private static MaaWin32Controller CreateController(IntPtr hwnd) => new(hwnd,
        Win32ScreencapMethods.GDI, Win32InputMethod.SendMessage, Win32InputMethod.SendMessage,
        (LinkOption)0, (CheckStatusOption)0);

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Passed.Add(name);
    }

    private static void ExpectCanceled(Task task)
    {
        try { task.GetAwaiter().GetResult(); throw new Exception("cancellation was ignored"); }
        catch (OperationCanceledException) { }
    }

    private static IntPtr CreateFixture(string title)
    {
        // An owned off-screen fixture without focus or a taskbar entry. The
        // native toolkit deliberately excludes windows lacking WS_VISIBLE.
        var hwnd = CreateWindowExW(0x08000080, "STATIC", title, 0x10CF0000, -32000, -32000, 64, 64,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        return hwnd;
    }

    private static void DestroyFixture(ref IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (!DestroyWindow(hwnd)) throw new System.ComponentModel.Win32Exception();
        hwnd = IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
