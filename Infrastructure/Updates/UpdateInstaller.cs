using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace QuietIsland;

internal sealed class UpdateInstaller
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{A7F2A8AF-9A67-4759-9B84-B0B3ECA08737}_is1";
    public string? RegisteredDirectory
    {
        get
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                using var key = root.OpenSubKey(UninstallKey);
                if (key?.GetValue("InstallLocation") is string path && IsSameDirectory(AppContext.BaseDirectory, path) &&
                    File.Exists(Path.Combine(path, "unins000.exe"))) return Path.GetFullPath(path);
            }
            return null;
        }
    }
    internal static bool IsSameDirectory(string current, string registered)
    {
        try { return string.Equals(Path.GetFullPath(current).TrimEnd('\\', '/'), Path.GetFullPath(registered).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    public void Launch(string path, UpdateAsset asset, bool startup)
    {
        var directory = RegisteredDirectory ?? throw new InvalidOperationException("This is a portable copy. Download the portable update from GitHub.");
        // Hold the verified file against modification/deletion until Windows starts it.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != asset.Size || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(file), Convert.FromHexString(asset.Sha256)))
            throw new InvalidDataException("Installer verification failed.");
        using var process = Process.Start(CreateStartInfo(path, directory, startup)) ?? throw new InvalidOperationException("Could not start the installer.");
        // The existing installer asks this instance to exit only when installation starts.
        // Cancelling its wizard therefore leaves the current widget running.
    }
    internal static ProcessStartInfo CreateStartInfo(string path, string directory, bool startup)
    {
        var info = new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(path))! };
        info.ArgumentList.Add("/DIR=" + Path.GetFullPath(directory));
        info.ArgumentList.Add("/MERGETASKS=" + (startup ? "startup" : "!startup"));
        info.ArgumentList.Add("/NORESTART");
        info.ArgumentList.Add("/SP-");
        return info;
    }
}
