using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MFAAvalonia.Helper;

/// <summary>
/// An elevated replacement must not open the instance mutex or command pipe
/// until its predecessor has finished shutting down.
/// </summary>
public static class RestartHandoff
{
    private const string ArgumentPrefix = "--restart-parent=";

    public static void AddParentArgument(ProcessStartInfo startInfo)
    {
        using var parent = Process.GetCurrentProcess();
        startInfo.ArgumentList.Add(FormattableString.Invariant(
            $"{ArgumentPrefix}{parent.Id}:{parent.StartTime.ToUniversalTime().Ticks}"));
    }

    public static string[] WaitForParentExit(
        string[] args, string executablePath, int timeoutMilliseconds = 15000)
    {
        var handoffArguments = args.Where(arg => arg.StartsWith(ArgumentPrefix, StringComparison.Ordinal)).ToArray();
        if (handoffArguments.Length == 0)
            return args;

        var remainingArgs = args.Where(arg => !arg.StartsWith(ArgumentPrefix, StringComparison.Ordinal)).ToArray();
        var identity = handoffArguments[0][ArgumentPrefix.Length..].Split(':');
        if (handoffArguments.Length != 1 || identity.Length != 2
            || !int.TryParse(identity[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parentId)
            || !long.TryParse(identity[1], NumberStyles.None, CultureInfo.InvariantCulture, out var startTimeTicks)
            || parentId <= 0 || parentId == Environment.ProcessId || startTimeTicks <= 0)
        {
            throw new ArgumentException("Invalid restart parent identity.");
        }

        Process parent;
        try
        {
            parent = Process.GetProcessById(parentId);
        }
        catch (ArgumentException)
        {
            // The predecessor may have exited before the replacement started.
            return remainingArgs;
        }

        using (parent)
        {
            if (parent.HasExited)
                return remainingArgs;

            try
            {
                var comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (parent.StartTime.ToUniversalTime().Ticks != startTimeTicks
                    || !string.Equals(Path.GetFullPath(parent.MainModule?.FileName ?? ""),
                        Path.GetFullPath(executablePath), comparison))
                {
                    throw new InvalidOperationException("The restart parent does not match this executable.");
                }
            }
            catch (InvalidOperationException) when (parent.HasExited)
            {
                return remainingArgs;
            }
            catch (System.ComponentModel.Win32Exception) when (parent.HasExited)
            {
                return remainingArgs;
            }

            if (!parent.WaitForExit(timeoutMilliseconds))
                throw new TimeoutException("The previous application did not exit. Restart was cancelled.");
        }

        return remainingArgs;
    }
}
