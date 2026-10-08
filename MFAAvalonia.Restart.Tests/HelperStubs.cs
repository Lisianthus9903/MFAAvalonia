namespace MFAAvalonia.Helper;

// The production instance guard is linked unchanged. These replace only its
// logging/UI dispatcher dependencies; the probes release on the owner thread.
public static class LoggerHelper
{
    public static void Warning(string message) => Console.Error.WriteLine(message);
    public static void Error(string message, Exception exception) => Console.Error.WriteLine(message);
}

public static class DispatcherHelper
{
    public static Task RunOnMainThreadAsync(Action action) =>
        throw new InvalidOperationException("A mutex was released on the wrong test thread.");
}
