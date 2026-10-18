using WslRemoteSetup.Core.Wsl;

namespace WslRemoteSetup.UnitTests;

public class WslOutputParserTests
{
    [Fact]
    public void Parses_names_and_ignores_noise()
    {
        var output = "﻿Ubuntu-24.04\r\n\r\ndocker-desktop\r\n";
        Assert.Equal(["Ubuntu-24.04", "docker-desktop"], WslOutputParser.ParseQuietList(output));
    }

    [Fact]
    public void Empty_output_means_none() => Assert.Empty(WslOutputParser.ParseQuietList(""));

    [Fact]
    public void Localized_message_lines_are_not_names() =>
        Assert.Empty(WslOutputParser.ParseQuietList("실행 중인 배포가 없습니다.\r\n"));

    [Fact]
    public void Embedded_nul_from_utf16_misdecode_is_removed() =>
        Assert.Equal(["Ubuntu"], WslOutputParser.ParseQuietList("U\0b\0u\0n\0t\0u\0\r\0\n\0"));
}
