using System.Globalization;
using System.Runtime.InteropServices;

namespace FishFillets.Cli;

/// <summary>
/// The two Windows regional settings that decide how a CSV opens in Excel: the
/// list separator and the decimal separator (Region ► Additional settings).
///
/// Read from the OS rather than from <see cref="CultureInfo.CurrentCulture"/>,
/// because this project builds with <c>InvariantGlobalization</c> - deliberately,
/// for Native AOT: no ICU dependency, no culture tables, faster startup, and a
/// solver that formats a move string identically everywhere. Under that setting
/// every culture behaves as the invariant one, so <c>TextInfo.ListSeparator</c>
/// always answers "," no matter how Windows is configured. Asking Win32 directly
/// costs one P/Invoke and keeps both properties.
///
/// These are also user *overrides*, not just the locale default - someone on a
/// Czech machine who has set the separator to a comma gets a comma, which the
/// culture tables would not have told us either.
/// </summary>
internal static partial class WindowsLocale
{
    private const uint LocaleSList = 0x0000000C;

    private const uint LocaleSDecimal = 0x0000000E;

    /// <summary>The Windows list separator (";" in cs/de/fr, "," in en), "," if unknown.</summary>
    public static string ListSeparator() => Query(LocaleSList, CultureInfo.CurrentCulture.TextInfo.ListSeparator);

    /// <summary>The Windows decimal separator ("," in cs/de/fr, "." in en), "." if unknown.</summary>
    public static string DecimalSeparator() =>
        Query(LocaleSDecimal, CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);

    /// <summary>
    /// Reads one LOCALE_* string for the user's default locale, falling back to
    /// whatever .NET believes off Windows (where this whole question is moot -
    /// no Excel, and the tool is Windows-only anyway).
    /// </summary>
    private static string Query(uint what, string fallback)
    {
        if (!OperatingSystem.IsWindows())
        {
            return fallback;
        }

        try
        {
            Span<char> buffer = stackalloc char[16];
            int written;
            unsafe
            {
                fixed (char* data = buffer)
                {
                    // A null locale name is LOCALE_NAME_USER_DEFAULT.
                    written = GetLocaleInfoEx(null, what, data, buffer.Length);
                }
            }

            // The count includes the terminating null.
            return written > 1 ? new string(buffer[..(written - 1)]) : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetLocaleInfoEx", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int GetLocaleInfoEx(string? localeName, uint lcType, char* data, int size);
}
