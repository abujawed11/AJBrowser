using System.Text;
using AJBrowser.Core.Blocking;

namespace AJBrowser.Core.Tests.Blocking;

public sealed class RedirectResourceTests
{
    [Fact]
    public void Decodes_base64_data_urls()
    {
        var resource = RedirectResource.TryDecode("data:application/javascript;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("(function(){})();")))!;
        Assert.Equal("application/javascript", resource.ContentType);
        Assert.Equal("(function(){})();", Encoding.UTF8.GetString(resource.Body));
    }

    [Fact]
    public void Decodes_percent_encoded_data_urls_keeping_plus_signs()
    {
        var resource = RedirectResource.TryDecode("data:text/plain,a%20b+c")!;
        Assert.Equal("text/plain", resource.ContentType);
        Assert.Equal("a b+c", Encoding.UTF8.GetString(resource.Body));
    }

    [Fact]
    public void Missing_type_means_text()
    {
        Assert.Equal("text/plain", RedirectResource.TryDecode("data:;base64,")!.ContentType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/noop.js")]
    [InlineData("data:text/plain")]
    [InlineData("data:text/plain;base64,***")]
    [InlineData("data:text/plain\r\nX-Evil: 1;base64,AA==")]
    public void Rejects_anything_else(string? url) => Assert.Null(RedirectResource.TryDecode(url));

    [Fact]
    public void Refuses_oversized_bodies()
    {
        var big = Convert.ToBase64String(new byte[RedirectResource.MaxBodyBytes + 1]);
        Assert.Null(RedirectResource.TryDecode("data:application/octet-stream;base64," + big));
    }

    [Fact]
    public void Works_with_what_the_engine_hands_out()
    {
        var resources = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "ubo-resources.json"));
        using var engine = AdblockRustEngine.Build([new FilterListText("||ads.example/px.gif$image,redirect=1x1.gif")], resources);
        var decision = engine.Check("https://ads.example/px.gif", "https://news.test/", RequestKind.Image);

        var resource = RedirectResource.TryDecode(decision.RedirectDataUrl)!;
        Assert.Equal("image/gif", resource.ContentType);
        Assert.Equal("GIF8"u8.ToArray(), resource.Body[..4]);
    }
}
