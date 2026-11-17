using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using KnowledgeMemory.Core;

namespace KnowledgeMemory.Host.Security;

/// <summary>
/// The machine API token lives in DataDirectory\secrets\api-token, readable only by the current Windows user.
/// It is never put in URLs, HTML, logs, CLI arguments or example configs.
/// </summary>
public static class TokenStore
{
    public static string EnsureToken(AppOptions o)
    {
        CreatePrivateDirectory(o.SecretsDirectory);
        if (File.Exists(o.TokenPath))
        {
            var existing = File.ReadAllText(o.TokenPath).Trim();
            if (existing.Length >= 32) return existing;
        }
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var tmp = o.TokenPath + ".tmp";
        File.WriteAllText(tmp, token, new UTF8Encoding(false));
        RestrictFile(tmp);
        File.Move(tmp, o.TokenPath, overwrite: true);
        return token;
    }

    public static string ReadToken(AppOptions o)
    {
        if (!File.Exists(o.TokenPath))
            throw new KnowledgeException(ErrorCodes.ServiceUnavailable,
                $"API token not found in {o.SecretsDirectory}. Start the service first: KnowledgeMemory.exe serve --config \"<path to appsettings.json>\"");
        return File.ReadAllText(o.TokenPath).Trim();
    }

    public static bool FixedTimeEquals(string? a, string b) =>
        a is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static void CreatePrivateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            RestrictDirectory(path);
            return;
        }
        Directory.CreateDirectory(path);
        RestrictDirectory(path);
    }

    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(sec);
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(sec);
    }

    private static SecurityIdentifier CurrentUser()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return WindowsIdentity.GetCurrent().User!;
    }
}
