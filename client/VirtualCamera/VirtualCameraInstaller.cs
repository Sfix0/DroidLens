using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DroidLens.Client;

namespace DroidLens.Client.VirtualCamera;

public static class VirtualCameraInstaller
{
    // CLSID Softcam (from their code)
    private const string Clsid = "{AEF3B972-5FA5-4647-9571-358EB472BC9E}";

    public static bool IsInstalled()
    {
        // Check if softcam.dll is registered in the registry
        using var key = Registry.LocalMachine
            .OpenSubKey($@"SOFTWARE\Classes\CLSID\{Clsid}");
        return key != null;
    }

    /// <summary>
    /// The InprocServer32 path currently registered for the Softcam CLSID, or
    /// null if the CLSID isn't registered at all. This is a raw registry read —
    /// it does NOT verify the path still exists on disk.
    /// </summary>
    public static string? GetRegisteredDllPath()
    {
        using var key = Registry.LocalMachine
            .OpenSubKey($@"SOFTWARE\Classes\CLSID\{Clsid}\InprocServer32");
        return key?.GetValue(null) as string; // (Default) value
    }

    /// <summary>
    /// True when the CLSID is registered but points at a dll path that either
    /// no longer matches the current build's location or no longer exists on
    /// disk. This is exactly the situation that silently breaks the camera for
    /// every external DirectShow consumer (OBS, Discord, browsers) while the
    /// app's own P/Invoke calls keep working fine — those load the dll that
    /// sits next to the running exe directly, bypassing the registry entirely,
    /// so IsInstalled() alone can't detect a stale registration like this.
    /// The most common real-world cause: the project folder was moved,
    /// renamed, or reinstalled elsewhere after the original regsvr32 call, and
    /// nothing ever re-registered it against the new path.
    /// </summary>
    public static bool IsRegistrationStale(string currentDllPath)
    {
        string? registered = GetRegisteredDllPath();
        if (registered is null)
            return false; // not registered at all — that's IsInstalled()'s job to report, not "stale"

        try
        {
            string full1 = Path.GetFullPath(registered);
            string full2 = Path.GetFullPath(currentDllPath);
            bool samePath = string.Equals(full1, full2, StringComparison.OrdinalIgnoreCase);
            return !samePath || !File.Exists(full1);
        }
        catch
        {
            // malformed path in the registry — treat as stale so a re-register is attempted
            return true;
        }
    }

    /// <summary>
    /// Registers softcam.dll — call once on first launch
    /// (administrator rights required)
    /// </summary>
    public static bool Install(string dllPath)
    {
        try
        {
            var psi = new ProcessStartInfo("regsvr32.exe", $"/s \"{dllPath}\"")
            {
                UseShellExecute = true,
                Verb            = "runas", // запит UAC
                CreateNoWindow  = true
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(10_000);
            return proc?.ExitCode == 0;
        }
        catch (Exception ex)
        {
            AppLog.E("VCamInstaller", ex, () => "Install failed");
            return false;
        }
    }

    public static bool Uninstall(string dllPath)
    {
        try
        {
            var psi = new ProcessStartInfo("regsvr32.exe", $"/s /u \"{dllPath}\"")
            {
                UseShellExecute = true,
                Verb            = "runas",
                CreateNoWindow  = true
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(10_000);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }
}