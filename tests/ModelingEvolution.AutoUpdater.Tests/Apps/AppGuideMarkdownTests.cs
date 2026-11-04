using System.Diagnostics;
using System.Text;
using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelingEvolution.AutoUpdater.Apps;
using ModelingEvolution.AutoUpdater.Host.Components.Shared;
using MudBlazor;
using MudBlazor.Services;

namespace ModelingEvolution.AutoUpdater.Tests.Apps;

/// <summary>
/// Guides through the component that ships: AppGuideView, i.e. MudMarkdown with AppGuideMarkdown.Pipeline. Each hostile
/// case also asserts what does show, so an empty render can never pass.
/// </summary>
public sealed class AppGuideMarkdownTests : IAsyncLifetime
{
    private readonly BunitContext _context = new();

    public AppGuideMarkdownTests()
    {
        _context.Services.AddMudServices();
        _context.Services.AddMudMarkdownServices();
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _context.DisposeAsync();

    private IRenderedComponent<AppGuideView> Render(string markdown) =>
        _context.Render<AppGuideView>(p => p.Add(v => v.Markdown, markdown));

    private static string Text(IRenderedComponent<AppGuideView> guide) =>
        string.Join(" ", guide.Nodes.Select(n => n.TextContent)).ReplaceLineEndings(" ");

    private static void NothingActive(IRenderedComponent<AppGuideView> guide)
    {
        guide.FindAll("script, img, iframe, object, embed, [onclick], [onerror], [onload]").Should().BeEmpty();
    }

    [Fact]
    public void OrdinaryMarkdown_RendersParagraphsListsEmphasisAndHttpLinks()
    {
        var guide = Render("**Requirements:** Android 12.\n\n1. Download it.\n2. See [the vendor](https://example.com/help).\n");

        guide.FindAll("ol li").Should().HaveCount(2);
        guide.Find("b, strong").TextContent.Should().Be("Requirements:");
        var link = guide.Find("a");
        link.GetAttribute("href").Should().Be("https://example.com/help");
        link.TextContent.Should().Be("the vendor");
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("before <script>alert(1)</script> after")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<div onclick=\"alert(1)\">x</div>")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>")]
    [InlineData("<svg onload=alert(1)>")]
    public void RawHtml_ShowsAsText_AndIsNeverInterpreted(string markdown)
    {
        var guide = Render(markdown);

        NothingActive(guide);
        Text(guide).Should().Contain(markdown, "the HTML shows as the text it is");
    }

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](JAVASCRIPT:alert(1))")]
    [InlineData("[click](<javascript:alert(1)>)")]
    [InlineData("[click](java&#x73;cript:alert(1))")]
    [InlineData("[click](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("[click](vbscript:msgbox)")]
    [InlineData("[click](file:///etc/passwd)")]
    [InlineData("[click](//evil.example/x)")]
    [InlineData("[click](\\\\evil.example\\x)")]
    [InlineData("[click][ref]\n\n[ref]: javascript:alert(1)")]
    [InlineData("# [click](javascript:alert(1))")]
    public void LinkToAnythingButHttpHttpsOrRelative_KeepsOnlyItsText(string markdown)
    {
        var guide = Render(markdown);

        guide.FindAll("a").Should().BeEmpty();
        Text(guide).Should().Contain("click");
        NothingActive(guide);
    }

    [Theory]
    [InlineData("<javascript:alert(1)>", "javascript:alert(1)")]
    [InlineData("<mailto:a@example.com>", "mailto:a@example.com")]
    [InlineData("<a@example.com>", "a@example.com")]
    public void AutolinkToAnythingButHttpOrHttps_ShowsAsText(string markdown, string text)
    {
        var guide = Render(markdown);

        guide.FindAll("a").Should().BeEmpty();
        Text(guide).Should().Contain(text);
    }

    [Fact]
    public void LinkText_StaysInPlace_WhenTheTargetGoes()
    {
        Text(Render("Open [the settings](javascript:alert(1)) now.")).Trim().Should().Be("Open the settings now.");
    }

    [Theory]
    [InlineData("[ok](https://example.com)", "https://example.com")]
    [InlineData("[ok](http://example.com/a?b#c)", "http://example.com/a?b#c")]
    [InlineData("[ok](apps/files/ExampleViewer-2.1.0.msi)", "apps/files/ExampleViewer-2.1.0.msi")]
    public void HttpHttpsAndRelativeLinks_AreRenderedWithTheirTarget(string markdown, string href)
    {
        var link = Render(markdown).Find("a");

        link.GetAttribute("href").Should().Be(href);
        link.TextContent.Should().Be("ok");
    }

    [Fact]
    public void Autolink_WithAnHttpsTarget_IsRendered()
    {
        var guide = Render("A <https://example.com/x> B");

        var link = guide.Find("a");
        link.GetAttribute("href").Should().Be("https://example.com/x");
        link.TextContent.Should().Be("https://example.com/x");
        Text(guide).Should().Contain("A https://example.com/x B");
    }

    [Fact]
    public void LinkInAHeading_IsRendered()
    {
        var guide = Render("# [Docs](https://example.com/d)");

        guide.Find("h1 a").GetAttribute("href").Should().Be("https://example.com/d");
    }

    [Fact]
    public void HtmlEntities_ShowAsTheirCharacters()
    {
        Text(Render("Tom &amp; Jerry &copy; 2026 &lt;b&gt;")).Trim().Should().Be("Tom & Jerry © 2026 <b>");
    }

    [Theory]
    [InlineData("![tracking pixel](https://evil.example/p.png)", "tracking pixel")]
    [InlineData("![x](javascript:alert(1))", "x")]
    [InlineData("![i](shot.png)", "i")]
    [InlineData("![x][r]\n\n[r]: shot.png", "x")]
    [InlineData("![a [b](javascript:alert(1)) c](https://evil.example/p.png)", "a b c")]
    public void Images_AreNeverShown_OnlyTheirAltTextOnce(string markdown, string alt)
    {
        var guide = Render(markdown);

        NothingActive(guide);
        guide.FindAll("a").Should().BeEmpty();
        Text(guide).Trim().Should().Be(alt);
    }

    [Fact]
    public void FencedCode_WithAHostileLanguage_ShowsTheCodeAndNothingActive()
    {
        var guide = Render("```\"><img src=x onerror=alert(1)>\nsudo apt install ./viewer.deb\n```");

        NothingActive(guide);
        // MudMarkdown hands the code to highlight.js, which escapes it; the language string stays an encoded attribute.
        _context.JSInterop.Invocations.SelectMany(i => i.Arguments).OfType<string>()
            .Should().Contain(a => a.Contains("sudo apt install ./viewer.deb"));
        guide.Find("pre code").Should().NotBeNull();
    }

    [Fact]
    public void Decode_ValidUtf8_ReturnsTheText_WithoutABom()
    {
        AppGuideMarkdown.Decode([0xEF, 0xBB, 0xBF, .. "# Hi"u8]).Should().Be("# Hi");
    }

    [Fact]
    public void Decode_InvalidUtf8_IsUnavailable()
    {
        AppGuideMarkdown.Decode([0x61, 0xFF, 0x62]).Should().BeNull();
        AppGuideMarkdown.Decode([0x61, 0xC3]).Should().BeNull();
    }

    [Fact]
    public void Decode_OverTheCapOrEmpty_IsUnavailable()
    {
        AppGuideMarkdown.MaxBytes.Should().Be(16 * 1024);
        AppGuideMarkdown.Decode(Encoding.UTF8.GetBytes(new string('a', AppGuideMarkdown.MaxBytes + 1))).Should().BeNull();
        AppGuideMarkdown.Decode(Encoding.UTF8.GetBytes(new string('a', AppGuideMarkdown.MaxBytes))).Should().NotBeNull();
        AppGuideMarkdown.Decode([]).Should().BeNull();
        AppGuideMarkdown.Decode("  \n "u8).Should().BeNull();
    }

    [Fact]
    public void Decode_NestingMarkdigRefuses_IsUnavailable_SoTheRendererNeverThrows()
    {
        var markdown = new string('>', 5000) + " deep";

        AppGuideMarkdown.Decode(Encoding.UTF8.GetBytes(markdown)).Should().BeNull();
    }

    [Fact]
    public void LargeOrdinaryGuide_AtTheCap_IsAcceptedAndRenders()
    {
        var markdown = string.Concat(Enumerable.Repeat("1. A step with **bold** and `code`.\n", 400));
        Encoding.UTF8.GetByteCount(markdown).Should().BeLessThanOrEqualTo(AppGuideMarkdown.MaxBytes);

        var text = AppGuideMarkdown.Decode(Encoding.UTF8.GetBytes(markdown));

        text.Should().NotBeNull();
        Render(text!).FindAll("ol li").Should().HaveCount(400);
    }

    public static TheoryData<string, string> Pathological() => new()
    {
        { "unclosed links", "[a](" },
        { "nested brackets", "[" },
        { "emphasis runs", "*a_" },
        { "backticks", "`a``" },
    };

    /// <summary>
    /// The worst guides found: parse cost grows faster than size, so the cap is what bounds them. The checking parse (off the
    /// page's render path) and the render (on it) each stay well under a second at the cap; the bound allows for CI.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pathological))]
    public void PathologicalGuide_AtTheCap_DecodesAndRendersWithinTwoSecondsEach(string what, string unit)
    {
        var markdown = string.Concat(Enumerable.Repeat(unit, AppGuideMarkdown.MaxBytes / unit.Length));

        var decode = Stopwatch.StartNew();
        var text = AppGuideMarkdown.Decode(Encoding.UTF8.GetBytes(markdown));
        decode.Stop();
        var render = Stopwatch.StartNew();
        if (text != null) Render(text);
        render.Stop();

        decode.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), what);
        render.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), what);
    }
}
