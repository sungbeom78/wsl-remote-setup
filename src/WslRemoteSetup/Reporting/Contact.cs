namespace WslRemoteSetup.Reporting;

/// <summary>문의·의견 채널 안내(도움말, 마법사 마지막 화면, 오류 화면, status 하단).</summary>
internal static class Contact
{
    public const string Url = "https://www.edubeom.com/";

    public static string Line => $"문의·의견: {Url}";

    public static void Show()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  {Line}");
        Console.ResetColor();
    }
}
