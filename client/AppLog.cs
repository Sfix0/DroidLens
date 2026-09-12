using System.Diagnostics;

namespace DroidLens.Client;

/// <summary>
/// Lightweight debug-only logger. Mirrors the Android-side AppLog.kt pattern:
/// lambda-based messages (so string formatting is skipped entirely in Release),
/// nothing ever reaches a console window, output only goes to the IDE's Debug
/// Output window (visible while running under a debugger), and Release builds
/// pay effectively zero cost since the whole call site is compiled out.
/// </summary>
public static class AppLog
{
    [Conditional("DEBUG")]
    public static void D(string tag, Func<string> message) =>
        Debug.WriteLine(message(), tag);

    [Conditional("DEBUG")]
    public static void I(string tag, Func<string> message) =>
        Debug.WriteLine(message(), tag);

    [Conditional("DEBUG")]
    public static void W(string tag, Func<string> message) =>
        Debug.WriteLine($"WARN: {message()}", tag);

    [Conditional("DEBUG")]
    public static void E(string tag, Func<string> message) =>
        Debug.WriteLine($"ERROR: {message()}", tag);

    [Conditional("DEBUG")]
    public static void E(string tag, Exception ex, Func<string> message) =>
        Debug.WriteLine($"ERROR: {message()} — {ex.Message}", tag);
}