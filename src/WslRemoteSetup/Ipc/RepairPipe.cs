using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using WslRemoteSetup.Core.Ipc;

namespace WslRemoteSetup.Ipc;

/// <summary>
/// 소유자 에이전트 ↔ 네트워크 관리부 named pipe (ADR 0003).
/// 서버는 ACL로 접근을 제한하고, 메시지 내용이 아니라 파이프 가장으로 호출자 SID를 확인한다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RepairPipe
{
    public static string PipeName(Guid installationId) => $"WslRemoteSetup.Net.{installationId:N}";

    /// <summary>SYSTEM·Administrators FullControl, 소유자 ReadWrite, NETWORK 로그온 거부.</summary>
    public static PipeSecurity CreateSecurity(SecurityIdentifier ownerSid)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(ownerSid,
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    /// <summary>폐기하며 읽어 줄 수 있는 초과 메시지의 상한. 이보다 크면 응답 없이 연결을 끊는다.</summary>
    internal const int DiscardLimitBytes = 64 * 1024;

    /// <summary>
    /// 메시지 하나를 읽는다. maxBytes를 넘으면 내용은 보관하지 않고 메시지 끝까지(상한 내) 버린 뒤 null을 돌려준다.
    /// 끝까지 버려야 쓰기 중인 상대가 막히지 않고 거부 응답을 받을 수 있다.
    /// </summary>
    internal static async Task<byte[]?> ReadMessageAsync(PipeStream pipe, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[maxBytes + 1];
        var total = 0;
        do
        {
            var n = await pipe.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
            if (total > maxBytes)
            {
                await DiscardRestAsync(pipe, total, ct).ConfigureAwait(false);
                return null;
            }
        }
        while (!pipe.IsMessageComplete);
        return buffer.AsSpan(0, total).ToArray();
    }

    private static async Task DiscardRestAsync(PipeStream pipe, int alreadyRead, CancellationToken ct)
    {
        var scratch = new byte[4096];
        var total = alreadyRead;
        while (!pipe.IsMessageComplete)
        {
            var n = await pipe.ReadAsync(scratch, ct).ConfigureAwait(false);
            if (n == 0) return;
            total += n;
            if (total > DiscardLimitBytes) throw new InvalidDataException("메시지가 폐기 상한을 넘었습니다.");
        }
    }
}

/// <summary>호출자 SID와 요청(또는 파싱 오류)을 받아 응답을 만드는 처리기.</summary>
public delegate RepairResponse RepairHandler(string? callerSid, RepairRequest? request, string parseError);

[SupportedOSPlatform("windows")]
public sealed class RepairPipeServer(string pipeName, PipeSecurity security, RepairHandler handler)
{
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>연결 하나를 처리한다. 서비스 모드에서는 반복 호출한다.</summary>
    public async Task ServeOnceAsync(CancellationToken ct)
    {
        await using var server = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: RepairRequest.MaxBytes,
            outBufferSize: RepairResponse.MaxBytes,
            security);

        await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

        string? callerSid = null;
        try
        {
            server.RunAsClient(() =>
            {
                using var id = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                callerSid = id.User?.Value;
            });
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReadTimeout);
        RepairResponse response;
        try
        {
            var bytes = await RepairPipe.ReadMessageAsync(server, RepairRequest.MaxBytes, timeout.Token).ConfigureAwait(false);
            if (bytes is null)
                response = handler(callerSid, null, "요청 크기 초과");
            else if (RepairRequest.TryParse(bytes, out var request, out var error))
                response = handler(callerSid, request, "");
            else
                response = handler(callerSid, null, error);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            response = new RepairResponse(RepairCode.Denied, "요청 시간 초과");
        }
        catch (InvalidDataException)
        {
            server.Disconnect(); // 과도한 입력: 응답 없이 끊는다
            return;
        }

        if (server.IsConnected)
        {
            try
            {
                await server.WriteAsync(response.ToUtf8(), ct).ConfigureAwait(false);
                await server.FlushAsync(ct).ConfigureAwait(false);
                server.WaitForPipeDrain();
            }
            catch (IOException) { } // 클라이언트가 먼저 끊은 경우
        }
    }
}

[SupportedOSPlatform("windows")]
public static class RepairPipeClient
{
    /// <summary>
    /// 요청을 보내고 응답을 받는다. 가장 수준은 Identification으로 제한해 서버가 신원 확인만 할 수 있게 한다.
    /// </summary>
    public static Task<RepairResponse> SendAsync(string pipeName, RepairRequest request, TimeSpan timeout, CancellationToken ct = default) =>
        SendRawAsync(pipeName, request.ToUtf8(), timeout, ct);

    internal static async Task<RepairResponse> SendRawAsync(string pipeName, byte[] payload, TimeSpan timeout, CancellationToken ct)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await client.ConnectAsync(cts.Token).ConfigureAwait(false);
        client.ReadMode = PipeTransmissionMode.Message;
        try
        {
            await client.WriteAsync(payload, cts.Token).ConfigureAwait(false);
            await client.FlushAsync(cts.Token).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // 서버가 크기 초과로 읽기를 중단하고 응답한 경우에도 응답은 읽어 본다.
        }
        var bytes = await RepairPipe.ReadMessageAsync(client, RepairResponse.MaxBytes, cts.Token).ConfigureAwait(false);
        return bytes is null ? new RepairResponse(RepairCode.Error, "응답 크기 초과") : RepairResponse.Parse(bytes);
    }
}
