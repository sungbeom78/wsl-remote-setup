using System.Net;
using System.Text;
using WslRemoteSetup.Core.Ipc;

namespace WslRemoteSetup.UnitTests;

public class RepairRequestTests
{
    private static readonly Guid Id = Guid.Parse("6f1c2a0e-1111-4c4c-9a9a-0123456789ab");

    private static bool Parse(string json, out RepairRequest? r, out string error) =>
        RepairRequest.TryParse(Encoding.UTF8.GetBytes(json), out r, out error);

    [Fact]
    public void Round_trip()
    {
        var original = new RepairRequest(Id, IPAddress.Parse("172.24.220.111"));
        Assert.True(RepairRequest.TryParse(original.ToUtf8(), out var parsed, out _));
        Assert.Equal(original.InstallationId, parsed!.InstallationId);
        Assert.Equal(original.ObservedWslIPv4, parsed.ObservedWslIPv4);
    }

    [Theory]
    [InlineData("""{"v":1,"installationId":"6f1c2a0e-1111-4c4c-9a9a-0123456789ab","observedWslIPv4":"172.24.220.111","listenPort":22}""", "허용되지 않은 필드")]
    [InlineData("""{"v":1,"installationId":"6f1c2a0e-1111-4c4c-9a9a-0123456789ab","observedWslIPv4":"172.24.220.111","cmd":"netsh"}""", "허용되지 않은 필드")]
    [InlineData("""{"v":1,"v":1,"installationId":"6f1c2a0e-1111-4c4c-9a9a-0123456789ab","observedWslIPv4":"172.24.220.111"}""", "중복")]
    [InlineData("""{"v":2,"installationId":"6f1c2a0e-1111-4c4c-9a9a-0123456789ab","observedWslIPv4":"172.24.220.111"}""", "버전")]
    [InlineData("""{"v":"1","installationId":"6f1c2a0e-1111-4c4c-9a9a-0123456789ab","observedWslIPv4":"172.24.220.111"}""", "값 오류")]
    [InlineData("""{"v":1,"installationId":"6f1c2a0e-1111-4c4c-9a9a-0123456789ab","observedWslIPv4":"172.24.220.111; calc"}""", "값 오류")]
    [InlineData("""{"v":1,"installationId":"6f1c2a0e-1111-4c4c-9a9a-0123456789ab","observedWslIPv4":"10.1"}""", "값 오류")]
    [InlineData("""{"v":1,"installationId":"6f1c2a0e-1111-4c4c-9a9a-0123456789ab"}""", "누락")]
    [InlineData("""{"v":1,"installationId":{"x":1},"observedWslIPv4":"172.24.220.111"}""", "")]
    [InlineData("""[1,2,3]""", "객체가 아님")]
    [InlineData("""{"v":1,"installationId":"6f1c2a0e-1111-4c4c-9a9a-0123456789ab","observedWslIPv4":"172.24.220.111"} {}""", "")]
    [InlineData("not json", "")]
    public void Tampered_requests_are_rejected(string json, string expectedError)
    {
        Assert.False(Parse(json, out var r, out var error));
        Assert.Null(r);
        Assert.Contains(expectedError, error);
    }

    [Fact]
    public void Oversized_request_is_rejected_before_parsing()
    {
        var big = new byte[RepairRequest.MaxBytes + 1];
        Assert.False(RepairRequest.TryParse(big, out _, out var error));
        Assert.Contains("크기", error);
    }

    [Fact]
    public void Response_round_trip_and_garbage()
    {
        var r = RepairResponse.Parse(new RepairResponse(RepairCode.Conflict, "충돌").ToUtf8());
        Assert.Equal(RepairCode.Conflict, r.Code);
        Assert.Equal("충돌", r.Message);
        Assert.Equal(RepairCode.Error, RepairResponse.Parse("{\"code\":\"Hack\"}"u8).Code);
    }
}
