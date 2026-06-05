// Copyright (c) fork author. Based on Echoglossian runtime patterns.
// Licensed under the same license terms as your Echoglossian fork.

namespace Echoglossian.TooltipOverlay;

/// <summary>
/// Default values for the CN tooltip overlay feature.
///
/// These are intentionally kept outside Config.cs so existing user config files
/// continue loading even before the Config.cs patch is applied.
/// </summary>
internal static class TooltipOverlayConfigDefaults
{
    public const bool TranslateTooltipOverlay = true;
    public const bool TooltipOverlayShowOriginal = false;
    public const int TooltipOverlayDelayMs = 350;
    public const int TooltipOverlayMaxWidth = 560;
    public const float TooltipOverlayFontScale = 1.0f;
    public const float TooltipOverlayBgAlpha = 0.96f;

    // If Dalamud keeps reporting the last hovered action after the cursor has left it,
    // hide the overlay once the mouse drifts this far from the point where the hover began.
    public const int TooltipOverlayMaxMouseDriftPixels = 96;

    // Debug patch: show source/translation/native scrape diagnostics directly in the overlay.
    public const bool TooltipOverlayDebug = false;
}
