using System;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

/// <summary>Refreshes a missing or expired window selection before controller creation.</summary>
internal static class Win32TargetRecovery
{
    public static async Task<bool> EnsureReadyAsync(
        Func<bool> isValid,
        Action invalidate,
        Func<CancellationToken, Task> rediscover,
        bool allowDiscovery,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (isValid())
            return true;

        invalidate();
        token.ThrowIfCancellationRequested();
        if (!allowDiscovery)
            return false;

        await rediscover(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return isValid();
    }
}
