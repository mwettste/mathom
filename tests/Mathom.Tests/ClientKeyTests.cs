using System.Net;
using Mathom.Web.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mathom.Tests;

public class ClientKeyTests
{
    private static DefaultHttpContext Ctx(string? header = null, string? remote = null, string? headerName = "CF-Connecting-IP")
    {
        var services = new ServiceCollection();
        services.Configure<AuthRateLimitOptions>(o => o.ClientIpHeader = headerName);
        var ctx = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (header is not null) ctx.Request.Headers["CF-Connecting-IP"] = header;
        if (remote is not null) ctx.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        return ctx;
    }

    [Fact]
    public void Ipv6_SameSlash64_SharesKey() =>
        Assert.Equal(AuthRateLimiting.ClientKey(Ctx("2001:db8:1:2::1")),
            AuthRateLimiting.ClientKey(Ctx("2001:db8:1:2:ffff::9")));

    [Fact]
    public void Ipv6_DifferentSlash64_DoesNotShareKey() =>
        Assert.NotEqual(AuthRateLimiting.ClientKey(Ctx("2001:db8:1:2::1")),
            AuthRateLimiting.ClientKey(Ctx("2001:db8:1:3::1")));

    [Fact]
    public void Ipv6_RendersAsSlash64Prefix() =>
        Assert.Equal("2001:db8:1:2::/64", AuthRateLimiting.ClientKey(Ctx("2001:db8:1:2:ffff::9")));

    [Fact]
    public void Ipv4Mapped_KeysLikeIpv4() =>
        Assert.Equal(AuthRateLimiting.ClientKey(Ctx("1.2.3.4")), AuthRateLimiting.ClientKey(Ctx("::ffff:1.2.3.4")));

    [Theory]
    [InlineData("1.1.1.1, 2.2.2.2")]
    [InlineData("garbage")]
    [InlineData("")]
    public void UnparseableHeader_FallsBackToRemoteAddress(string header) =>
        Assert.Equal("9.9.9.9", AuthRateLimiting.ClientKey(Ctx(header, remote: "9.9.9.9")));

    [Fact]
    public void UnparseableHeader_NoRemoteAddress_IsUnknown() =>
        Assert.Equal("unknown", AuthRateLimiting.ClientKey(Ctx("garbage")));

    [Fact]
    public void RemoteIpv6_UsesSlash64() =>
        Assert.Equal("2001:db8:1:2::/64", AuthRateLimiting.ClientKey(Ctx(remote: "2001:db8:1:2::77", headerName: null)));

    [Fact]
    public void HeaderNotConfigured_IgnoresHeader() =>
        Assert.Equal("9.9.9.9", AuthRateLimiting.ClientKey(Ctx("1.1.1.1", remote: "9.9.9.9", headerName: null)));
}
