using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FishFillets.Search;

/// <summary>
/// The processor time the calling thread has used, in 100 ns ticks (TimeSpan
/// ticks). The per-window time limit is charged in it, not by the wall clock.
///
/// A wall clock keeps running while the machine sleeps. Pavement's windows of
/// 44 came back from 13.5 minutes of Modern Standby with 14 minutes on the
/// clock and 2 of real work, and four of them were cut on the spot. A
/// throttled CPU or a busy machine skews it the same way. Thread time counts
/// only the work the window got.
/// </summary>
internal static class ThreadClock
{
    private static readonly bool Windows = OperatingSystem.IsWindows();

    public static long Now()
    {
        if (Windows && GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user))
        {
            return kernel + user;
        }

        // Elsewhere, the wall clock, in the same units.
        return (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();
}
