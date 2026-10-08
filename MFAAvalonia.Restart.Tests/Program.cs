using System.Diagnostics;
using System.Text.Json;
using MFAAvalonia;
using MFAAvalonia.Helper;

internal static class Program
{
    private sealed record ProbeResult(bool? IsNewInstance, bool TimedOut, long ElapsedMs, bool InternalArgumentRemoved);
    private static int _checks;

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "parent") return Parent(args);
        if (args.Length > 0 && args[0] == "child") return Child(args);
        if (args.Length > 0 && args[0] == "sleep") { Thread.Sleep(30000); return 0; }

        var work = Path.Combine(Path.GetTempPath(), "MFA-Restart-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var baseline = RunScenario(work, "baseline");
            Check(baseline.IsNewInstance == false, "old launch order reproduces duplicate-instance status");
            Check(baseline.ElapsedMs >= 600, "baseline retains false status after predecessor releases its mutex");

            var handoff = RunScenario(work, "handoff");
            Check(handoff.IsNewInstance == true, "replacement acquires the production instance guard after parent exit");
            Check(handoff.ElapsedMs >= 250, "replacement actually waits for the predecessor");
            Check(handoff.InternalArgumentRemoved, "internal restart identity is removed before normal argument parsing");

            var duplicate = RunScenario(work, "duplicate");
            Check(duplicate.IsNewInstance == false, "ordinary duplicate launch remains blocked");

            var timeout = RunScenario(work, "timeout");
            Check(timeout.TimedOut && timeout.IsNewInstance == null,
                "a predecessor that stays open times out before any mutex or recovery/kill logic");

            var exited = RunScenario(work, "exited");
            Check(exited.IsNewInstance == true, "an already exited predecessor is accepted");

            for (var i = 0; i < 10; i++)
                Check(RunScenario(work, "fast", i).IsNewInstance == true, "fast parent-exit race " + i);

            var ordinary = new[] { "--instance", "프로필 1", "--quit-after-run" };
            Check(ReferenceEquals(RestartHandoff.WaitForParentExit(ordinary, Environment.ProcessPath!), ordinary),
                "ordinary startup arguments are untouched");
            Reject<ArgumentException>(["--restart-parent=invalid"], "malformed identity");
            Reject<ArgumentException>(["--restart-parent=0:1"], "zero PID");
            Reject<ArgumentException>([$"--restart-parent={Environment.ProcessId}:1"], "self wait");
            Reject<ArgumentException>(["--restart-parent=1:1", "--restart-parent=2:2"], "duplicate identity");
            using var other = Start("sleep");
            try
            {
                Reject<InvalidOperationException>([$"--restart-parent={other.Id}:{other.StartTime.ToUniversalTime().Ticks + 1}"],
                    "reused PID/start time mismatch");
                Reject<InvalidOperationException>([$"--restart-parent={other.Id}:{other.StartTime.ToUniversalTime().Ticks}"],
                    "different executable", Path.Combine(work, "different.exe"));
            }
            finally
            {
                other.Kill();
                other.WaitForExit(5000);
            }

            Console.WriteLine($"PASS: {_checks} restart/instance checks; no GUI, UAC or game automation invoked.");
            return 0;
        }
        finally
        {
            // Only this test's fresh, absolute temporary directory is removed.
            Directory.Delete(work, recursive: true);
        }
    }

    private static ProbeResult RunScenario(string work, string mode, int index = 0)
    {
        var resultPath = Path.Combine(work, mode + index + ".json");
        var key = "RestartTest_" + Guid.NewGuid().ToString("N");
        using var parent = Start("parent", mode, key, resultPath);
        if (!parent.WaitForExit(10000) || parent.ExitCode != 0)
            throw new Exception("Parent probe failed: " + mode);
        var watch = Stopwatch.StartNew();
        while (!File.Exists(resultPath) && watch.ElapsedMilliseconds < 10000) Thread.Sleep(20);
        if (!File.Exists(resultPath)) throw new Exception("Child probe failed: " + mode);
        // The child publishes by renaming a complete JSON file.
        return JsonSerializer.Deserialize<ProbeResult>(File.ReadAllText(resultPath))!;
    }

    private static int Parent(string[] args)
    {
        var mode = args[1];
        AppRuntime.Initialize([], args[2]);
        var info = StartInfo("child", mode, args[2], args[3]);
        if (mode is "handoff" or "timeout" or "exited" or "fast") RestartHandoff.AddParentArgument(info);
        using var child = Process.Start(info)!;
        if (mode == "duplicate")
        {
            if (!child.WaitForExit(5000)) return 1;
        }
        else if (mode is not "exited" and not "fast")
        {
            var watch = Stopwatch.StartNew();
            while (!File.Exists(args[3] + ".started") && watch.ElapsedMilliseconds < 5000) Thread.Sleep(10);
            Thread.Sleep(400);
        }
        AppRuntime.ReleaseMutex();
        return 0;
    }

    private static int Child(string[] args)
    {
        var watch = Stopwatch.StartNew();
        File.WriteAllText(args[3] + ".started", "ready");
        bool? isNew = null;
        var timedOut = false;
        var internalRemoved = false;
        try
        {
            if (args[1] == "exited") Thread.Sleep(300);
            var remaining = RestartHandoff.WaitForParentExit(args, Environment.ProcessPath!, args[1] == "timeout" ? 100 : 5000);
            internalRemoved = remaining.All(arg => !arg.StartsWith("--restart-parent="));
            AppRuntime.Initialize(remaining, args[2]);
            if (args[1] == "baseline") Thread.Sleep(700);
            isNew = AppRuntime.IsNewInstance;
            AppRuntime.ReleaseMutex();
        }
        catch (TimeoutException) { timedOut = true; }
        var path = args[3];
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new ProbeResult(isNew, timedOut, watch.ElapsedMilliseconds, internalRemoved)));
        File.Move(path + ".tmp", path);
        return 0;
    }

    private static ProcessStartInfo StartInfo(params string[] args)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    private static Process Start(params string[] args) => Process.Start(StartInfo(args))!;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        _checks++;
        Console.WriteLine("PASS: " + message);
    }

    private static void Reject<T>(string[] args, string message, string? executablePath = null) where T : Exception
    {
        try { RestartHandoff.WaitForParentExit(args, executablePath ?? Environment.ProcessPath!, 100); }
        catch (T) { Check(true, "reject " + message); return; }
        throw new Exception("FAIL: accepted " + message);
    }
}
