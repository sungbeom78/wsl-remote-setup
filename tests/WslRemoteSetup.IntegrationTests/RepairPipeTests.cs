using System.IO.Pipes;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using WslRemoteSetup.Core.Ipc;
using WslRemoteSetup.Ipc;

namespace WslRemoteSetup.IntegrationTests;

/// <summary>
/// 같은 사용자로 임시 파이프를 열어 IPC 경계를 확인한다. 시스템 설정을 바꾸지 않는다.
/// 다른 로컬 사용자 거부(T10)는 별도 테스트 계정이 필요해 여기서 다루지 않는다.
/// </summary>
public class RepairPipeTests
{
    private static readonly Guid Id = Guid.Parse("6f1c2a0e-1111-4c4c-9a9a-0123456789ab");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static string CurrentSid()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.User!.Value;
    }

    private static string UniqueName() => "WslRemoteSetup.Test." + Guid.NewGuid().ToString("N");

    private static async Task<(RepairResponse Response, string? CallerSid, RepairRequest? Request)> RoundTripAsync(
        Func<string, Task<RepairResponse>> send, PipeSecurity? security = null)
    {
        var name = UniqueName();
        string? caller = null;
        RepairRequest? received = null;
        var server = new RepairPipeServer(name, security ?? RepairPipe.CreateSecurity(new SecurityIdentifier(CurrentSid())),
            (sid, req, error) =>
            {
                caller = sid;
                received = req;
                return req is null ? new RepairResponse(RepairCode.Denied, error) : new RepairResponse(RepairCode.NoChange, "ok");
            });
        using var cts = new CancellationTokenSource(Timeout);
        var serving = server.ServeOnceAsync(cts.Token);
        var response = await send(name);
        await serving;
        return (response, caller, received);
    }

    [Fact]
    public async Task Server_identifies_caller_by_pipe_not_by_message()
    {
        var (response, caller, request) = await RoundTripAsync(name =>
            RepairPipeClient.SendAsync(name, new RepairRequest(Id, IPAddress.Parse("172.24.220.111")), Timeout));

        Assert.Equal(RepairCode.NoChange, response.Code);
        Assert.Equal(CurrentSid(), caller);
        Assert.Equal("172.24.220.111", request!.ObservedWslIPv4.ToString());
    }

    [Fact]
    public async Task Tampered_payload_is_rejected_at_server()
    {
        var payload = Encoding.UTF8.GetBytes("""{"v":1,"installationId":"6f1c2a0e-1111-4c4c-9a9a-0123456789ab","observedWslIPv4":"172.24.220.111","listenAddress":"0.0.0.0"}""");
        var (response, caller, request) = await RoundTripAsync(name =>
            RepairPipeClient.SendRawAsync(name, payload, Timeout, CancellationToken.None));

        Assert.Equal(RepairCode.Denied, response.Code);
        Assert.Null(request);
        Assert.Equal(CurrentSid(), caller);
    }

    [Fact]
    public async Task Oversized_payload_is_rejected_without_full_read()
    {
        var payload = new byte[RepairRequest.MaxBytes * 4];
        Array.Fill(payload, (byte)'a');
        var (response, _, request) = await RoundTripAsync(name =>
            RepairPipeClient.SendRawAsync(name, payload, Timeout, CancellationToken.None));

        Assert.Equal(RepairCode.Denied, response.Code);
        Assert.Contains("크기", response.Message);
        Assert.Null(request);
    }

    [Fact]
    public async Task Acl_without_current_user_denies_connection()
    {
        // 소유자로 다른 SID(로컬 Guest 계열 임의 SID)를 지정하면 현재 사용자는 접근이 거부되어야 한다.
        var stranger = new SecurityIdentifier("S-1-5-21-1-2-3-4242");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(stranger, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        var name = UniqueName();
        var server = new RepairPipeServer(name, security, (_, _, _) => new RepairResponse(RepairCode.NoChange, ""));
        using var cts = new CancellationTokenSource(Timeout);
        var serving = server.ServeOnceAsync(cts.Token);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            RepairPipeClient.SendAsync(name, new RepairRequest(Id, IPAddress.Parse("172.24.220.111")), TimeSpan.FromSeconds(3)));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serving);
    }

    [Fact]
    public void Pipe_security_denies_network_and_limits_owner_to_read_write()
    {
        var owner = new SecurityIdentifier(CurrentSid());
        var rules = RepairPipe.CreateSecurity(owner).GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();

        Assert.Contains(rules, r => r.IdentityReference.Value == "S-1-5-2" && r.AccessControlType == AccessControlType.Deny);
        var ownerRule = Assert.Single(rules, r => r.IdentityReference.Value == owner.Value);
        Assert.Equal(AccessControlType.Allow, ownerRule.AccessControlType);
        Assert.False(ownerRule.PipeAccessRights.HasFlag(PipeAccessRights.ChangePermissions));
        Assert.False(ownerRule.PipeAccessRights.HasFlag(PipeAccessRights.TakeOwnership));
        Assert.DoesNotContain(rules, r => r.IdentityReference.Value is "S-1-1-0" or "S-1-5-11"); // Everyone, Authenticated Users 없음
    }
}
