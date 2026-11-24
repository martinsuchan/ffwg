using System.Runtime.InteropServices;

namespace FishFillets.Cli;

/// <summary>
/// Opts the process out of Windows power throttling (EcoQoS).
///
/// Windows 11 treats a process with no visible window and a lowered priority
/// as background work. It runs that work at a low clock, and Task Manager shows
/// it as "efficiency mode". A detached long search is exactly that. On a
/// Snapdragon X Elite it got about 0.3 of a core per worker: 12 threads
/// showed as "Ready" but shared about 3 cores, with the rest of the machine
/// idle. The cost is the search's own time, so the search turns this off. The
/// lowered priority stays, so the machine still answers to its user first.
/// </summary>
internal static partial class PowerThrottling
{
    private const int ProcessPowerThrottling = 4;          // PROCESS_INFORMATION_CLASS
    private const uint ExecutionSpeed = 0x1;                // PROCESS_POWER_THROTTLING_EXECUTION_SPEED
    private const uint IgnoreTimerResolution = 0x4;         // PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION
    private const uint CurrentVersion = 1;                  // PROCESS_POWER_THROTTLING_CURRENT_VERSION

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    /// <summary>Turns throttling off for this process; true if Windows accepted it. A no-op elsewhere.</summary>
    public static bool Disable()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return false;
        }

        // Control the two bits, and clear both: never throttle, keep timer resolution.
        var state = new State { Version = CurrentVersion, ControlMask = ExecutionSpeed | IgnoreTimerResolution, StateMask = 0 };
        return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<State>());
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(IntPtr process, int informationClass, ref State information, uint size);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();
}
