using WslRemoteSetup.Core.Wsl;

namespace WslRemoteSetup.UnitTests;

public class DistroBackupTests
{
    private const string Def = @"C:\Users\u\Documents\WSL 백업\Ubuntu-24.04-20261019-0100.tar";

    [Fact]
    public void Default_path_has_distro_and_time() =>
        Assert.Equal(Def, DistroBackup.DefaultPath(@"C:\Users\u\Documents", "Ubuntu-24.04", new DateTimeOffset(2026, 10, 19, 1, 0, 0, TimeSpan.FromHours(9))));

    [Theory]
    [InlineData("", Def)]
    [InlineData("  ", Def)]
    [InlineData(@"D:\backup\", @"D:\backup\Ubuntu-24.04-20261019-0100.tar")]
    [InlineData(@"""D:\backup\my""", @"D:\backup\my.tar")]
    [InlineData(@"D:\backup\my.tar", @"D:\backup\my.tar")]
    public void Resolve_input(string input, string expected) =>
        Assert.Equal(expected, DistroBackup.Resolve(input, Def, _ => false));

    [Fact]
    public void Existing_folder_gets_default_file_name() =>
        Assert.Equal(@"E:\bk\Ubuntu-24.04-20261019-0100.tar", DistroBackup.Resolve(@"E:\bk", Def, p => p == @"E:\bk"));

    [Fact]
    public void Check_passes_with_enough_space() =>
        Assert.Null(DistroBackup.Check(@"D:\b\x.tar", false, 10L << 30, 3L << 30));

    [Theory]
    [InlineData(@"backup.tar", false)]
    [InlineData(@"\\wsl.localhost\Ubuntu-24.04\home\x.tar", false)]
    [InlineData(@"D:\b\x.zip", false)]
    [InlineData(@"D:\b\x.tar", true)]
    public void Check_rejects_bad_targets(string path, bool exists) =>
        Assert.NotNull(DistroBackup.Check(path, exists, null, null));

    [Fact]
    public void Check_rejects_low_space_in_plain_words()
    {
        var e = DistroBackup.Check(@"D:\b\x.tar", false, 3L << 30, 3L << 30);
        Assert.Contains("공간이 부족", e, StringComparison.Ordinal);
        Assert.Contains("GB", e, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_df_used() => Assert.Equal(1234567L, DistroBackup.ParseDfUsed("   Used\n1234567\n"));

    [Fact]
    public void Restore_command_names_distro_and_file() =>
        Assert.Equal(@"wsl --import Ubuntu-24.04 ""%LOCALAPPDATA%\WSL\Ubuntu-24.04"" ""D:\b\x.tar""", DistroBackup.RestoreCommand("Ubuntu-24.04", @"D:\b\x.tar"));
}
