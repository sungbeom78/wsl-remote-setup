using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WslRemoteSetup.Ipc;

/// <summary>
/// named pipe 상대방(클라이언트)의 사용자 SID를 확인한다.
/// 클라이언트는 Identification 수준만 허용하므로, 가장 중인 스레드 토큰을 OpenAsSelf=TRUE로 열어야 한다
/// (.NET RunAsClient + WindowsIdentity.GetCurrent는 SYSTEM 서비스에서 이 토큰을 열지 못했다 — G2 실기 결함).
/// 가장은 반드시 같은 스레드에서 RevertToSelf로 되돌린다.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class PipeCaller
{
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenUser = 1;

    public static string GetClientSid(SafePipeHandle pipe)
    {
        if (!ImpersonateNamedPipeClient(pipe)) throw new Win32Exception(Marshal.GetLastPInvokeError(), "ImpersonateNamedPipeClient");
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, true, out token))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenThreadToken");
        }
        finally
        {
            if (!RevertToSelf()) Environment.FailFast("RevertToSelf 실패: 가장 상태로 계속 실행할 수 없습니다.");
        }

        try
        {
            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var size);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenUser, buffer, size, out _))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetTokenInformation");
                var sidPtr = Marshal.ReadIntPtr(buffer); // TOKEN_USER.User.Sid
                return new SecurityIdentifier(sidPtr).Value;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ImpersonateNamedPipeClient(SafePipeHandle hNamedPipe);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RevertToSelf();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenThreadToken(IntPtr thread, uint access, [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out IntPtr token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
