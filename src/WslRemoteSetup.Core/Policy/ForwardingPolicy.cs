using System.Net;
using System.Text.Json;
using WslRemoteSetup.Core.Ipc;
using WslRemoteSetup.Core.Network;
using WslRemoteSetup.Core.State;
using WslRemoteSetup.Core.Validation;

namespace WslRemoteSetup.Core.Policy;

/// <summary>
/// 관리자가 승인해 보호 경로(SYSTEM·Administrators 쓰기 전용)에 저장하는 포워딩 정책.
/// 네트워크 관리부는 이 정책과 자신이 직접 관찰한 사실만으로 요청을 판단한다.
/// </summary>
public sealed record ForwardingPolicy
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required Guid InstallationId { get; init; }
    public required string OwnerSid { get; init; }
    public required string ListenAddress { get; init; }
    public required int ListenPort { get; init; }
    public required int ConnectPort { get; init; }
    public TimeSpan MinUpdateInterval { get; init; } = TimeSpan.FromSeconds(30);
    public IReadOnlyList<string> SshHostKeyFingerprints { get; init; } = [];
    /// <summary>DHCP 주소 변경 시 같은 카드·같은 LAN 범위 안에서 새 주소로 옮길지(ADR 0007).</summary>
    public bool FollowAddressChange { get; init; }
    /// <summary>수신 네트워크 카드 ID(Windows NetworkInterface.Id).</summary>
    public string? ListenInterfaceId { get; init; }
    /// <summary>승인 당시 LAN 범위(예: 192.168.1.0/24).</summary>
    public string? LanCidr { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var e = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion) e.Add("지원하지 않는 정책 버전");
        if (InstallationId == Guid.Empty) e.Add("installationId 없음");
        if (!OwnerSid.StartsWith("S-1-5-21-", StringComparison.Ordinal)) e.Add("ownerSid는 로컬/도메인 사용자 SID여야 합니다.");
        if (!InputValidator.TryParseIPv4(ListenAddress, out var listen))
            e.Add("listenAddress가 올바른 IPv4가 아닙니다.");
        else if (listen.Equals(IPAddress.Any) || Ipv4Cidr.IsLoopbackOrLinkLocal(listen))
            e.Add("listenAddress로 0.0.0.0·loopback·link-local을 사용할 수 없습니다.");
        if (!InputValidator.Port(ListenPort).IsValid) e.Add("listenPort 범위 오류");
        if (!InputValidator.Port(ConnectPort).IsValid) e.Add("connectPort 범위 오류");
        if (MinUpdateInterval < TimeSpan.FromSeconds(5)) e.Add("minUpdateInterval은 5초 이상이어야 합니다.");
        if (FollowAddressChange)
        {
            if (string.IsNullOrEmpty(ListenInterfaceId)) e.Add("주소 따라가기에는 네트워크 카드 ID가 필요합니다.");
            if (!Ipv4Cidr.TryParse(LanCidr, out var lan)) e.Add("주소 따라가기에는 LAN 범위가 필요합니다.");
            else if (lan.PrefixLength < 16) e.Add("LAN 범위가 너무 넓습니다(/16보다 넓은 범위는 허용하지 않음).");
            else if (InputValidator.TryParseIPv4(ListenAddress, out var la) && !lan.Contains(la)) e.Add("접속 주소가 LAN 범위 밖입니다.");
        }
        return e;
    }

    public static ForwardingPolicy Parse(string json)
    {
        var p = JsonSerializer.Deserialize<ForwardingPolicy>(json, StateStore.JsonOptions)
            ?? throw new InvalidDataException("빈 정책");
        var errors = p.Validate();
        if (errors.Count > 0) throw new InvalidDataException(string.Join(" ", errors));
        return p;
    }
}

public sealed record RepairDecision(RepairCode Code, string Reason)
{
    public bool Allowed => Code == RepairCode.Updated;
    public static RepairDecision Allow() => new(RepairCode.Updated, "허용");
    public static RepairDecision Deny(string reason) => new(RepairCode.Denied, reason);
}

/// <summary>네트워크 관리부의 요청 검증. 순수 함수이며 입력은 모두 관리부가 신뢰할 수 있는 출처에서 온다(요청 본문 제외).</summary>
public static class RepairRequestValidator
{
    /// <param name="callerSid">파이프 가장(impersonation)으로 확인한 호출자 SID.</param>
    /// <param name="hostWslCidr">관리부가 직접 조회한 WSL 호스트 가상 어댑터 대역. 없으면 거부.</param>
    /// <param name="lastUpdateAt">마지막으로 portproxy를 변경한 시각.</param>
    public static RepairDecision Evaluate(
        ForwardingPolicy policy,
        RepairRequest request,
        string? callerSid,
        Ipv4Cidr? hostWslCidr,
        DateTimeOffset now,
        DateTimeOffset? lastUpdateAt)
    {
        if (string.IsNullOrEmpty(callerSid) || !string.Equals(callerSid, policy.OwnerSid, StringComparison.OrdinalIgnoreCase))
            return RepairDecision.Deny("호출자가 정책의 소유자가 아닙니다.");
        if (request.InstallationId != policy.InstallationId)
            return RepairDecision.Deny("설치 ID가 일치하지 않습니다.");

        var ip = request.ObservedWslIPv4;
        if (!Ipv4Cidr.IsPrivate(ip) || Ipv4Cidr.IsLoopbackOrLinkLocal(ip))
            return RepairDecision.Deny("WSL 주소는 사설 IPv4여야 합니다.");
        if (InputValidator.TryParseIPv4(policy.ListenAddress, out var listen) && ip.Equals(listen))
            return RepairDecision.Deny("대상 주소가 수신 주소와 같습니다.");
        if (hostWslCidr is not { } cidr)
            return RepairDecision.Deny("WSL 호스트 가상 어댑터를 확인할 수 없습니다.");
        if (!cidr.Contains(ip))
            return RepairDecision.Deny("주소가 WSL NAT 대역 밖입니다.");

        if (lastUpdateAt is { } last && now - last < policy.MinUpdateInterval)
            return new RepairDecision(RepairCode.RateLimited, "갱신 간격 제한");

        return RepairDecision.Allow();
    }
}
