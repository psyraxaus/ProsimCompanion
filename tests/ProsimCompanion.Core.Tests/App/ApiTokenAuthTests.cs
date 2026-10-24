using System.Net;
using Microsoft.AspNetCore.Http;
using ProsimCompanion.App.Hosting;
using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.App;

/// <summary>The credential rules of the command/status APIs: bearer token or onboarded cookie,
/// token-even-on-loopback as a per-surface policy, and nothing in a query string.</summary>
public sealed class ApiTokenAuthTests
{
    private const string Master = "0123456789ABCDEF0123456789ABCDEF";

    private static DefaultHttpContext Lan(string? query = null, string? bearer = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.30");
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/command/gsx.forceNextService";
        if (query is not null)
        {
            context.Request.QueryString = new QueryString(query);
        }

        if (bearer is not null)
        {
            context.Request.Headers.Authorization = "Bearer " + bearer;
        }

        return context;
    }

    private static readonly WebUiOptions WebUi = new() { AccessToken = Master };

    [Fact]
    public void BearerToken_Passes()
        => Assert.True(ApiTokenAuth.IsAuthorized(Lan(bearer: Master), requireTokenOnLoopback: true, WebUi));

    [Fact]
    public void WrongBearer_IsRefused()
        => Assert.False(ApiTokenAuth.IsAuthorized(Lan(bearer: "nope"), true, WebUi));

    [Fact]
    public void TokenInQueryString_IsNeverAccepted()
        => Assert.False(ApiTokenAuth.IsAuthorized(Lan(query: "?token=" + Master), true, WebUi));

    [Fact]
    public void OnboardedCookie_Passes()
    {
        var context = Lan();
        context.Request.Headers.Cookie = LanTokenMiddleware.CookieName + "=" + Master;

        Assert.True(ApiTokenAuth.IsAuthorized(context, true, WebUi));
    }

    [Fact]
    public void Loopback_PassesOnlyWhenTheSurfaceSaysSo()
    {
        var context = Lan();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;

        Assert.True(ApiTokenAuth.IsAuthorized(context, requireTokenOnLoopback: false, WebUi));
        Assert.False(ApiTokenAuth.IsAuthorized(context, requireTokenOnLoopback: true, WebUi));
    }

    [Fact]
    public void NoTokenConfigured_StaysClosed()
        => Assert.False(ApiTokenAuth.IsAuthorized(Lan(bearer: ""), true, new WebUiOptions { AccessToken = "" }));
}
