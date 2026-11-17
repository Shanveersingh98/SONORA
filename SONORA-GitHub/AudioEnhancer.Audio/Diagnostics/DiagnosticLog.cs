using System.Diagnostics;

namespace AudioEnhancer.Audio;

/// <summary>
/// TEMPORARY - Part 4 Step 7 diagnosis only.
///
/// Writes short, throttled status lines so the application-side
/// capture -> DSP -> render pipeline can be inspected without attaching a debugger.
/// Every call site that uses this is tagged "Step 7 diagnostic" in a comment; once the
/// app-side path is confirmed working (or confirmed impossible on this machine), delete
/// this file and its call sites. Nothing here is required for the app to function -
/// setting <see cref="Enabled"/> to false (or removing the calls) returns the code to
/// exactly its pre-Step-7-diagnosis behaviour.
///
/// Two sinks, both best-effort and both swallow their own failures so a logging problem
/// can never take an audio thread down:
///  - <see cref="Debug.WriteLine(string)"/>: visible in the Visual Studio Output window,
///    or in Sysinternals DebugView, when nothing else is attached.
///  - A plain text file in <c>%TEMP%\AudioEnhancer-diagnostics.log</c> (appended to), so
///    the log survives without either of the above running - this is a WinExe with no
///    console, so a bare Console.WriteLine would otherwise go nowhere visible.
///
/// Call sites are throttled (by frame-count buckets, not a timer) to a few lines a
/// second at most on the capture/render threads, and are not expected to be
/// allocation-free - this is intentionally a temporary, diagnosis-only exception to the
/// "no I/O on the audio thread" rule the rest of this codebase follows.
/// </summary>
internal static class DiagnosticLog
{
    /// <summary>Flip to false to silence every Step 7 diagnostic line without removing the call sites.</summary>
    public static bool Enabled = true;

    private static readonly string FilePath =
        Path.Combine(Path.GetTempPath(), "AudioEnhancer-diagnostics.log");
    private static readonly object FileGate = new();

    public static void Write(string message)
    {
        if (!Enabled) return;

        Debug.WriteLine(message);

        try
        {
            lock (FileGate)
            {
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostic-only: a logging failure must never affect audio.
        }
    }
}
