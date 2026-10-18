using WslRemoteSetup.Setup;

namespace WslRemoteSetup.IntegrationTests;

/// <summary>단일 EXE에 Linux 스크립트가 포함되고 LF로 풀리는지(사용자 폴더에 쓰기만 함, 시스템 변경 없음).</summary>
public class EmbeddedScriptsTests
{
    [Theory]
    [InlineData("init-user.sh")]
    [InlineData("install-openssh.sh")]
    [InlineData("add-authorized-key.sh")]
    [InlineData("probe-ssh.sh")]
    [InlineData("verify-ssh.sh")]
    [InlineData("disable-password-auth.sh")]
    [InlineData("revert-ssh.sh")]
    public void Script_is_embedded_and_extracted_with_lf(string name)
    {
        var path = LinuxScripts.Extract(name);
        var text = File.ReadAllText(path);
        Assert.StartsWith("#!/bin/bash", text);
        Assert.DoesNotContain("\r\n", text);
    }

    [Theory]
    [InlineData(@"C:\Users\ownerAdmin\AppData\Local\WslRemoteSetup\scripts\a.sh", "/mnt/c/Users/ownerAdmin/AppData/Local/WslRemoteSetup/scripts/a.sh")]
    [InlineData(@"D:\x\y.sh", "/mnt/d/x/y.sh")]
    public void Windows_path_maps_to_wsl_mount(string windows, string expected) =>
        Assert.Equal(expected, LinuxScripts.ToWslPath(windows));
}
