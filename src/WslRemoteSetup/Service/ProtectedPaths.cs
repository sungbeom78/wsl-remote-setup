using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WslRemoteSetup.Service;

/// <summary>
/// 권한 있는 구성요소가 읽는 경로(개발 계획 10절). 일반 사용자가 쓸 수 있는 파일은 정책·실행 파일로 신뢰하지 않는다.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProtectedPaths
{
    public const string ServiceName = "WslRemoteSetupNet";

    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WslRemoteSetup");
    public static string InstalledExe => Path.Combine(InstallDirectory, "WslRemoteSetup.exe");
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WslRemoteSetup");
    public static string PolicyDirectory => Path.Combine(DataRoot, "policy");
    public static string PolicyFile => Path.Combine(PolicyDirectory, "forwarding.json");
    public static string StateFile => Path.Combine(DataRoot, "state", "install-state.json");
    public static string LogDirectory => Path.Combine(DataRoot, "logs");

    private static readonly SecurityIdentifier[] TrustedWriters =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // NT SERVICE\TrustedInstaller
    ];

    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership | FileSystemRights.DeleteSubdirectoriesAndFiles;

    /// <summary>파일과 그 상위 폴더에 신뢰 주체 외의 쓰기 허용 ACE가 없는지 확인한다. 소유자도 신뢰 주체여야 한다.</summary>
    public static bool IsAdminOnlyWritable(string path, out string reason)
    {
        try
        {
            foreach (var target in new[] { path, Path.GetDirectoryName(path)! })
            {
                FileSystemSecurity sec = File.Exists(target) ? new FileInfo(target).GetAccessControl() : new DirectoryInfo(target).GetAccessControl();
                var owner = sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                if (owner is null || !TrustedWriters.Contains(owner))
                {
                    reason = $"{target}의 소유자가 신뢰 주체가 아닙니다({owner?.Value}).";
                    return false;
                }
                foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    if (r.AccessControlType != AccessControlType.Allow || (r.FileSystemRights & WriteRights) == 0) continue;
                    var sid = (SecurityIdentifier)r.IdentityReference;
                    if (TrustedWriters.Contains(sid)) continue;
                    if (sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)) continue; // 소유자가 신뢰 주체임을 위에서 확인
                    reason = $"{target}에 {sid.Value}의 쓰기 권한이 있습니다.";
                    return false;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = ex.Message;
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>SYSTEM·Administrators 전체, Users 읽기, 상속 차단으로 폴더를 만든다.</summary>
    public static void EnsureProtectedDirectory(string path)
    {
        var dir = Directory.CreateDirectory(path);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);
        sec.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(sec);
    }
}
