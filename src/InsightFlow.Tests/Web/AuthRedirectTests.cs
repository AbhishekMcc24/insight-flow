using InsightFlow.Web.Services;

namespace InsightFlow.Tests.Web;

/// <summary>Values from the browser are checked before they become a redirect or a hint for the identity provider.</summary>
public sealed class AuthRedirectTests
{
    [Theory]
    [InlineData("/workspace", "/workspace")]
    [InlineData("/query?x=1", "/query?x=1")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\evil.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/login", "/")]
    [InlineData("/auth/signout", "/")]
    [InlineData("/ok\r\nSet-Cookie: x=1", "/")]
    public void Safe_ReturnUrl_OnlyKeepsSamePathsInsideTheApp(string? input, string expected) =>
        AuthRedirect.Safe(input).ShouldBe(expected);

    [Theory]
    [InlineData("a@b.com", "a@b.com")]
    [InlineData("  a@b.com ", "a@b.com")]
    [InlineData("Name <a@b.com>", null)]
    [InlineData("not-an-email", null)]
    [InlineData("", null)]
    public void CleanEmail_AcceptsOnlyABareAddress(string? input, string? expected) =>
        AuthRedirect.CleanEmail(input).ShouldBe(expected);
}
