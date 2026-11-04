using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ModelingEvolution.AutoUpdater.Apps;

/// <summary>
/// An install guide that passed <see cref="AppGuideMarkdown.Decode"/>: its text, and its document as parsed and cleaned up
/// by <see cref="AppGuideMarkdown.Pipeline"/>. Only Decode makes one, so a document is never one the cleanup did not see.
/// </summary>
public sealed class AppGuide
{
    internal AppGuide(string text, MarkdownDocument document)
    {
        Text = text;
        Document = document;
    }

    public string Text { get; }
    public MarkdownDocument Document { get; }
}

/// <summary>
/// The markdown rules for an app's install guide. The guide comes from the apps image, so it is untrusted: raw HTML is not
/// parsed (it stays visible as text), every image is replaced by its alt text, and a link keeps its target only when that is
/// http, https or relative; any other link (javascript:, data:, file:, protocol-relative //host, ...) is reduced to its text.
/// The cleanup runs on the parsed document, so it holds for whichever renderer gets <see cref="Pipeline"/>.
/// </summary>
public static class AppGuideMarkdown
{
    /// <summary>
    /// The largest guide the AutoUpdater reads; a larger one is "unavailable". An install guide is a page of steps; the cap
    /// also bounds the parse cost of a hostile guide, which grows faster than its size.
    /// </summary>
    public const int MaxBytes = 16 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static MarkdownPipeline Pipeline { get; } = CreatePipeline();

    private static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder().DisableHtml();
        builder.DocumentProcessed += Sanitize;
        return builder.Build();
    }

    /// <summary>
    /// The guide, parsed and cleaned up, or null when it is empty, too large, not valid UTF-8, or markdown that fails to
    /// parse (Markdig throws past its nesting limit). This is the only parse: the caller runs it off the page's render path,
    /// and the page renders <see cref="AppGuide.Document"/> as it is.
    /// </summary>
    public static AppGuide? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaxBytes) return null;
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) bytes = bytes[3..];
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return new AppGuide(text, Markdown.Parse(text, Pipeline));
        }
        catch (Exception)
        {
            // Any failure of an untrusted document makes it unavailable, whatever Markdig or the cleanup threw.
            return null;
        }
    }

    /// <summary>http or https, or a relative reference with no scheme that does not start with // or a backslash.</summary>
    public static bool IsAllowedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var trimmed = url.Trim();
        if (trimmed.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return false;
        if (trimmed.StartsWith("//") || trimmed.StartsWith('\\') || trimmed.StartsWith("/\\")) return false;

        var end = trimmed.IndexOfAny(['/', '?', '#']);
        var head = end < 0 ? trimmed : trimmed[..end];
        if (!head.Contains(':')) return true;

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute)
               && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps);
    }

    private static void Sanitize(MarkdownDocument document)
    {
        // Collected first: replacing while walking would invalidate the walk.
        var html = document.Descendants<HtmlBlock>().ToList();
        foreach (var block in html)
        {
            var paragraph = new ParagraphBlock { Inline = new ContainerInline().AppendChild(new LiteralInline(block.Lines.ToString())) };
            var parent = block.Parent!;
            parent[parent.IndexOf(block)] = paragraph;
        }

        foreach (var inline in document.Descendants<HtmlInline>().ToList())
            inline.ReplaceBy(new LiteralInline(inline.Tag ?? ""));

        // MudMarkdown renders neither of these two (it drops them silently), so both become what it does render.
        foreach (var entity in document.Descendants<HtmlEntityInline>().ToList())
            entity.ReplaceBy(new LiteralInline(entity.Transcoded.ToString()));

        foreach (var autolink in document.Descendants<AutolinkInline>().ToList())
        {
            var url = autolink.Url ?? "";
            Inline replacement = autolink.IsEmail || !IsAllowedUrl(url)
                ? new LiteralInline(url)
                : new LinkInline(url, "").AppendChild(new LiteralInline(url));
            autolink.ReplaceBy(replacement);
        }

        // Images first, and without their children: an image's alt text may itself hold a link, which goes with it.
        foreach (var image in document.Descendants<LinkInline>().Where(l => l.IsImage).ToList())
        {
            if (image.Parent == null) continue;
            image.ReplaceBy(new LiteralInline(TextOf(image)), copyChildren: false);
        }

        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            if (!IsAllowedUrl(link.Url)) Unwrap(link);
        }
    }

    /// <summary>Puts a link's children where the link was, so its text stays and its target goes.</summary>
    private static void Unwrap(LinkInline link)
    {
        Inline anchor = link;
        foreach (var child in link.ToList())
        {
            child.Remove();
            anchor.InsertAfter(child);
            anchor = child;
        }

        link.Remove();
    }

    private static string TextOf(ContainerInline container)
    {
        var text = new StringBuilder();
        foreach (var literal in container.Descendants<LiteralInline>()) text.Append(literal.Content.ToString());
        return text.ToString();
    }
}
