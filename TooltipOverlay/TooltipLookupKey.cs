// Copyright (c) fork author. Based on Echoglossian runtime patterns.
// Licensed under the same license terms as your Echoglossian fork.

using Dalamud.Game.Gui;

namespace Echoglossian.TooltipOverlay;

internal enum TooltipLookupKind
{
    None,
    Item,
    Action,
    GeneralAction,
    CraftingAction,
    Trait,
    UnknownActionLike,
}

internal readonly record struct TooltipLookupKey(
    TooltipLookupKind Kind,
    uint RowId,
    bool HighQuality = false,
    DetailKind DetailKind = DetailKind.None)
{
    public static TooltipLookupKey None { get; } = new(TooltipLookupKind.None, 0);

    public bool IsNone => this.Kind == TooltipLookupKind.None || this.RowId == 0;

    public string CacheKey => $"{this.Kind}:{this.RowId}:{this.HighQuality}:{this.DetailKind}";

    public string DisplayKind => this.Kind switch
    {
        TooltipLookupKind.Item => this.HighQuality ? "Item HQ" : "Item",
        TooltipLookupKind.Action => "Action",
        TooltipLookupKind.GeneralAction => "General Action",
        TooltipLookupKind.CraftingAction => "Crafting Action",
        TooltipLookupKind.Trait => "Trait",
        TooltipLookupKind.UnknownActionLike => this.DetailKind.ToString(),
        _ => string.Empty,
    };
}
