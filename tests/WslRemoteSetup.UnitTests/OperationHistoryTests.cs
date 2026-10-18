using WslRemoteSetup.Core.Reporting;

namespace WslRemoteSetup.UnitTests;

public sealed class OperationHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wrs-hist-" + Guid.NewGuid().ToString("N"), "기록");
    private string FilePath => Path.Combine(_dir, "history.log");

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_dir)!;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [Fact]
    public void Each_step_is_written_immediately_before_completion()
    {
        var h = new OperationHistory(FilePath, "자동 실행 설치", "ownerAdmin");
        h.Begin("실행 파일 설치");
        h.Detail("설치 ID", "5f3c9a10-7b2e-4d6a-9c1e-2a8b4d6f0e13");
        var midway = File.ReadAllText(FilePath);
        Assert.Contains("[1] 시작: 실행 파일 설치", midway);
        Assert.Contains("5f3c9a10-7b2e-4d6a-9c1e-2a8b4d6f0e13", midway);
        Assert.DoesNotContain("완료", midway);

        h.Done("설치함");
        h.Begin("정책 작성");
        h.Fail("권한 오류");
        h.Finish("실패");
        var all = File.ReadAllText(FilePath);
        Assert.Contains("[1] 완료: 설치함", all);
        Assert.Contains("[2] 실패: 권한 오류", all);
        Assert.Contains("끝: 실패", all);
    }

    [Fact]
    public void Runs_are_appended_not_overwritten()
    {
        new OperationHistory(FilePath, "첫 작업", "a").Finish("성공");
        new OperationHistory(FilePath, "둘째 작업", "a").Finish("성공");
        var all = File.ReadAllText(FilePath);
        Assert.Contains("첫 작업 시작", all);
        Assert.Contains("둘째 작업 시작", all);
    }
}
