using System.Runtime.Versioning;
using KnowledgeMemory.Core;
using Microsoft.Win32;

namespace KnowledgeMemory.Host.Tray;

/// <summary>
/// "Start with Windows": a value in HKCU\Software\Microsoft\Windows\CurrentVersion\Run (current user only,
/// no administrator rights) that starts the tray app at sign-in.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartup(string valueName = WindowsStartup.DefaultValueName)
{
    public const string DefaultValueName = "KnowledgeMemory";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string ValueName { get; } = valueName;

    public static string CommandFor(AppOptions o, string exePath) =>
        o.ConfigPath is null ? $"\"{exePath}\" tray" : $"\"{exePath}\" tray --config \"{o.ConfigPath}\"";

    public string? Current()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) as string;
    }

    public bool IsEnabled => Current() is not null;

    public void Enable(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
    }

    /// <summary>Makes the registry match Server.StartWithWindows for this exe and config.</summary>
    public void Apply(AppOptions o, string exePath)
    {
        if (o.Server.StartWithWindows) Enable(CommandFor(o, exePath));
        else Disable();
    }
}
