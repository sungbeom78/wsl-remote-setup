using System.Globalization;
using System.Text;

namespace WslRemoteSetup.Core.Reporting;

/// <summary>
/// 작업 기록(히스토리). 화면에는 쉬운 말만 보여 주고, 설치 ID·계정 SID·파일 확인값·서버 키 지문 같은
/// 기술 값은 이 기록에만 남긴다(ADR 0006). 단계가 진행될 때마다 즉시 파일에 덧붙여, 중간에 멈춰도
/// 어디까지 진행했는지 남는다. 비밀번호·개인키는 기록하지 않는다.
/// </summary>
public sealed class OperationHistory
{
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private int _step;

    public OperationHistory(string path, string operation, string actor, TimeProvider? time = null)
    {
        _path = Path.GetFullPath(path);
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        Append($"{Environment.NewLine}===== {Stamp()} {operation} 시작 (실행 계정: {actor})");
    }

    public string FilePath => _path;

    /// <summary>새 단계 시작. 번호를 붙인다.</summary>
    public void Begin(string title)
    {
        _step++;
        Append($"{Stamp()} [{_step}] 시작: {title}");
    }

    /// <summary>현재 단계의 기술 세부값(화면에는 표시하지 않음).</summary>
    public void Detail(string label, string? value) =>
        Append($"      - {label}: {value ?? "(없음)"}");

    public void Done(string message) => Append($"{Stamp()} [{_step}] 완료: {message}");

    public void Skip(string message) => Append($"{Stamp()} [{_step}] 변경 없음: {message}");

    public void Fail(string message) => Append($"{Stamp()} [{_step}] 실패: {message}");

    public void Note(string message) => Append($"{Stamp()} 참고: {message}");

    public void Finish(string result) => Append($"===== {Stamp()} 끝: {result}");

    private string Stamp() => _time.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private void Append(string line)
    {
        lock (_gate)
        {
            using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = new UTF8Encoding(false).GetBytes(line + Environment.NewLine);
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
    }
}
