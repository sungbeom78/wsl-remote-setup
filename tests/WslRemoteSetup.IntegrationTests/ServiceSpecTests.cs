using WslRemoteSetup.Forwarding;

namespace WslRemoteSetup.IntegrationTests;

public class ServiceSpecTests
{
    [Theory]
    [InlineData("web:8080:80", "web", 8080, 80)]
    [InlineData("DB:15432:5432", "db", 15432, 5432)]
    public void Parses_name_listen_connect(string spec, string name, int listen, int connect)
    {
        var s = ServiceForwarding.Parse(spec);
        Assert.NotNull(s);
        Assert.Equal((name, listen, connect), (s!.Name, s.ListenPort, s.ConnectPort));
    }

    [Theory]
    [InlineData("web")]
    [InlineData("web:8080")]
    [InlineData("web:80a:80")]
    [InlineData("web:-1:80")]
    [InlineData("web:8080:80:1")]
    public void Rejects_bad_specs(string spec) => Assert.Null(ServiceForwarding.Parse(spec));
}
