// SPDX-License-Identifier: MIT
//
// Windows autostart: writes a string value to HKCU\Software\Microsoft\
// Windows\CurrentVersion\Run so Windows launches the tray app on
// user login.  We use HKCU (current user) so we don't need
// administrator rights.
using Microsoft.Win32;

namespace RokidDiyStation.App;

public sealed class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "RokidDiyStation";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
    }

    /// <summary>Enable autostart.  The <paramref name="exePath"/> must be
    /// the actual path the user will launch from.  For development
    /// builds this is the dotnet host; for installed builds it's
    /// the .exe in the install dir.</summary>
    public void Enable(string exePath, bool startMinimised)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Cannot open HKCU Run key for writing");
        var args = startMinimised ? "--minimised" : "";
        key.SetValue(ValueName, $"\"{exePath}\" {args}".Trim(), RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) != null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
