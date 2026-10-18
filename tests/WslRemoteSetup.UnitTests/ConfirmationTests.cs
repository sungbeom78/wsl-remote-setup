using WslRemoteSetup.Core.Validation;

namespace WslRemoteSetup.UnitTests;

public class ConfirmationTests
{
    [Theory]
    [InlineData(null, ConfirmAnswer.Yes)]   // EOF가 아닌 빈 줄 입력 기준. 리디렉션 입력은 호출자가 먼저 거부한다.
    [InlineData("", ConfirmAnswer.Yes)]
    [InlineData("  ", ConfirmAnswer.Yes)]
    [InlineData("y", ConfirmAnswer.Yes)]
    [InlineData("Y", ConfirmAnswer.Yes)]
    [InlineData("yes", ConfirmAnswer.Yes)]
    [InlineData("n", ConfirmAnswer.No)]
    [InlineData("No", ConfirmAnswer.No)]
    [InlineData("적용", ConfirmAnswer.Invalid)]
    [InlineData("?섏슜", ConfirmAnswer.Invalid)] // CP949/UTF-8 불일치로 깨진 입력
    [InlineData("yy", ConfirmAnswer.Invalid)]
    public void Parse(string? input, ConfirmAnswer expected) => Assert.Equal(expected, Confirmation.Parse(input));
}
