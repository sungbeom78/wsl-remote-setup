using WslRemoteSetup.Service;

namespace WslRemoteSetup.IntegrationTests;

/// <summary>정책·실행 파일 신뢰 판정(읽기 전용 ACL 조회). 시스템 설정을 바꾸지 않는다.</summary>
public class ProtectedPathsTests
{
    [Fact]
    public void User_writable_temp_file_is_not_trusted()
    {
        var path = Path.Combine(Path.GetTempPath(), "wrs-acl-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{}");
        try
        {
            Assert.False(ProtectedPaths.IsAdminOnlyWritable(path, out var reason));
            Assert.NotEmpty(reason);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void System32_binary_is_trusted()
    {
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        Assert.True(ProtectedPaths.IsAdminOnlyWritable(notepad, out var reason), reason);
    }

    [Fact]
    public void Repository_build_output_is_not_trusted() // 저장소의 수정 가능한 파일을 관리자 실행 대상으로 쓰지 않음
    {
        var self = typeof(ProtectedPaths).Assembly.Location;
        Assert.False(ProtectedPaths.IsAdminOnlyWritable(self, out _));
    }

    [Fact]
    public void Missing_file_is_not_trusted() =>
        Assert.False(ProtectedPaths.IsAdminOnlyWritable(@"C:\definitely\missing\policy.json", out _));
}
