// Copyright (c) fork author. Based on Echoglossian runtime patterns.
// Licensed under the same license terms as your Echoglossian fork.

namespace Echoglossian.TooltipOverlay;

internal sealed record TooltipPayload(
    TooltipLookupKey Key,
    string OriginalTitle,
    string OriginalBody,
    string TranslatedTitle,
    string TranslatedBody,
    bool IsPending = false,
    string? Error = null)
{
    public bool HasTranslation =>
        !string.IsNullOrWhiteSpace(this.TranslatedTitle) ||
        !string.IsNullOrWhiteSpace(this.TranslatedBody);
}
