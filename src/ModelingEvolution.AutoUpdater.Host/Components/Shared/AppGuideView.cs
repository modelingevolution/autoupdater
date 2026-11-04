using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using ModelingEvolution.AutoUpdater.Apps;
using MudBlazor;

namespace ModelingEvolution.AutoUpdater.Host.Components.Shared;

/// <summary>
/// An app's install guide, rendered by MudMarkdown from the document <see cref="AppGuideMarkdown.Decode"/> already parsed
/// and cleaned up (off the render path). MudMarkdown itself would parse its Value again on every render; this renders the
/// given document as it is, so the circuit never parses an untrusted guide.
/// </summary>
public sealed class AppGuideView : MudMarkdown
{
    [Parameter, EditorRequired] public AppGuide? Guide { get; set; }

    protected override void OnParametersSet()
    {
        base.OnParametersSet();
        // Only for MudMarkdown's own messages that quote a span of the source; nothing is parsed from it.
        Value = Guide?.Text ?? string.Empty;
        MarkdownPipeline = AppGuideMarkdown.Pipeline;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Guide is not { } guide || guide.Document.Count == 0) return;
        var index = 0;
        RenderMarkdownRoot(builder, ref index, guide.Document);
    }
}
