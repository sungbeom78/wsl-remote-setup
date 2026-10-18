using WslRemoteSetup.Core.Validation;

namespace WslRemoteSetup.Core.Model;

/// <summary>SSH 로그인 방식(ADR 0005). 공개키 인증은 두 방식 모두 켠다.</summary>
public enum SshAuthentication
{
    /// <summary>비밀번호 + 공개키. LAN 한정 방화벽과 함께 사용(기본).</summary>
    PasswordAndPublicKey,
    /// <summary>공개키만. 외부 클라이언트에서 공개키 로그인을 확인한 뒤에만 비밀번호를 끈다.</summary>
    PublicKeyOnly,
}

/// <summary>사용자가 선택한 목표 구성. 비밀번호는 포함하지 않는다.</summary>
public sealed record DesiredConfiguration
{
    public const string DefaultDistro = "Ubuntu-24.04";
    public const string DefaultLinuxUser = "linux_admin";
    public const string DefaultNewWindowsAccount = "remoteControl";
    public const int DefaultListenPort = 1004;
    public const int DefaultWslSshPort = 22;

    /// <summary>WSL 배포판 소유 Windows 계정 이름. null이면 현재 사용자.</summary>
    public string? OwnerAccountName { get; init; }
    public string DistroName { get; init; } = DefaultDistro;
    public string LinuxUser { get; init; } = DefaultLinuxUser;
    public int ListenPort { get; init; } = DefaultListenPort;
    public int WslSshPort { get; init; } = DefaultWslSshPort;
    public bool ConfigureRdp { get; init; } = true;
    public SshAuthentication SshAuth { get; init; } = SshAuthentication.PasswordAndPublicKey;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        void Add(ValidationResult r) { if (!r.IsValid) errors.Add(r.Error!); }
        if (OwnerAccountName is not null) Add(InputValidator.WindowsAccountName(OwnerAccountName));
        Add(InputValidator.DistroName(DistroName));
        Add(InputValidator.LinuxUserName(LinuxUser));
        Add(InputValidator.Port(ListenPort));
        Add(InputValidator.Port(WslSshPort));
        return errors;
    }
}
