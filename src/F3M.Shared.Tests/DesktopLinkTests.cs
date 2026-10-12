using F3M.Shared.Helpers;

namespace F3M.Shared.Tests;

public class DesktopLinkTests
{
    [Theory]
    [InlineData("https://f3m.example", "https://f3m.example")]
    [InlineData("https://F3M.Example/", "https://f3m.example")]
    [InlineData("  https://f3m.example  ", "https://f3m.example")]
    [InlineData("https://f3m.example:443/", "https://f3m.example")]
    [InlineData("https://f3m.example:8443", "https://f3m.example:8443")]
    [InlineData("http://localhost:62445/", "http://localhost:62445")]
    public void TryNormalizeServer_GivesTheOrigin(string address, string expected)
    {
        Assert.True(DesktopLink.TryNormalizeServer(address, out var origin));
        Assert.Equal(expected, origin);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("f3m.example")]
    [InlineData("ftp://f3m.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://f3m.example/some/path")]
    [InlineData("https://f3m.example/?x=1")]
    [InlineData("https://f3m.example/#top")]
    [InlineData("https://user:pass@f3m.example")]
    public void TryNormalizeServer_RejectsAnythingButAnOrigin(string? address)
    {
        Assert.False(DesktopLink.TryNormalizeServer(address, out _));
    }

    [Fact]
    public void Download_NamesTheServer()
    {
        Assert.Equal("f3m://v1/download?mod=45&server=https%3A%2F%2Ff3m.example",
            DesktopLink.Download(45, null, "https://f3m.example/"));
        Assert.Equal("f3m://v1/download?mod=45&version=7&server=http%3A%2F%2Flocalhost%3A62445",
            DesktopLink.Download(45, 7, "http://localhost:62445/"));
    }

    [Fact]
    public void CollectionLink_NamesTheServer()
    {
        var link = new CollectionLink([45, 67], "My setup");
        Assert.Equal("f3m://v1/import-profile?mods=45,67&name=My%20setup&server=https%3A%2F%2Ff3m.example",
            link.ToProtocolUri("https://f3m.example/"));
    }

    [Fact]
    public void WithServer_RejectsAnInvalidServer()
    {
        Assert.Throws<ArgumentException>(() => DesktopLink.WithServer("f3m://v1/open?mod=1", "not a url"));
    }
}
