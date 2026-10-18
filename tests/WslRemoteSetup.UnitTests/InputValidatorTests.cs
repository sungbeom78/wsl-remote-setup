using WslRemoteSetup.Core.Validation;

namespace WslRemoteSetup.UnitTests;

public class InputValidatorTests
{
    [Theory]
    [InlineData("linux_admin")]
    [InlineData("_svc")]
    [InlineData("dev-user1")]
    public void LinuxUserName_accepts_valid(string name) =>
        Assert.True(InputValidator.LinuxUserName(name).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("Linux_Admin")]
    [InlineData("1user")]
    [InlineData("user name")]
    [InlineData("user;rm")]
    [InlineData("root")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")] // 33자
    public void LinuxUserName_rejects_invalid(string name) =>
        Assert.False(InputValidator.LinuxUserName(name).IsValid);

    [Theory]
    [InlineData("ownerAdmin")]
    [InlineData("remoteControl")]
    [InlineData("홍길동")]
    public void WindowsAccountName_accepts_valid(string name) =>
        Assert.True(InputValidator.WindowsAccountName(name).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a@b")]
    [InlineData("...")]
    [InlineData("name.")]
    [InlineData("abcdefghijklmnopqrstu")] // 21자
    public void WindowsAccountName_rejects_invalid(string name) =>
        Assert.False(InputValidator.WindowsAccountName(name).IsValid);

    [Theory]
    [InlineData("Ubuntu-24.04", true)]
    [InlineData("Ubuntu", true)]
    [InlineData("-bad", false)]
    [InlineData("Ubuntu 24", false)]
    [InlineData("Ubuntu;calc", false)]
    public void DistroName(string name, bool valid) =>
        Assert.Equal(valid, InputValidator.DistroName(name).IsValid);

    [Theory]
    [InlineData("1004", true)]
    [InlineData("0", false)]
    [InlineData("65536", false)]
    [InlineData("-1", false)]
    [InlineData("22a", false)]
    public void Port(string value, bool valid) =>
        Assert.Equal(valid, InputValidator.Port(value).IsValid);

    [Theory]
    [InlineData("192.168.1.222", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("10.1", false)]          // IPAddress.TryParse는 허용하지만 거부
    [InlineData("192.168.050.1", false)] // 선행 0
    [InlineData("256.1.1.1", false)]
    [InlineData("::1", false)]
    [InlineData("1.2.3.4 ", false)]
    public void IPv4(string value, bool valid) =>
        Assert.Equal(valid, InputValidator.IPv4(value).IsValid);
}
