using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using MaaFramework.Binding;
using MFAAvalonia;
using MFAAvalonia.Extensions.MaaFW;

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

            var report = new { passed = Passed.Count, checks = Passed,
                nativeVersion = NativeBindingContext.LibraryVersion, gameInput = false,
                fixtureOnly = true, source = typeof(MaaProcessor).Assembly.Location };
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
