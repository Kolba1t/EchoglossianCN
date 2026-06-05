// Copyright (c) fork author. Based on Echoglossian runtime patterns.
// Licensed under the same license terms as your Echoglossian fork.

using Dalamud.Game;
using Dalamud.Game.Gui;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace Echoglossian.TooltipOverlay;

internal sealed class GameTooltipTextProvider
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    public GameTooltipTextProvider(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    public TooltipPayload? BuildSourcePayload(TooltipLookupKey key)
    {
        if (key.IsNone)
        {
            return null;
        }

        try
        {
            return key.Kind switch
            {
                TooltipLookupKind.Item => this.BuildFromSheet<Item>(key, "Name", "Description"),
                TooltipLookupKind.Action => this.BuildFromSheet<LuminaAction>(key, "Name", "Description"),
                TooltipLookupKind.CraftingAction => this.BuildFromSheet<CraftAction>(key, "Name", "Description"),
                TooltipLookupKind.GeneralAction => this.BuildFromSheet<GeneralAction>(key, "Name", "Description"),
                TooltipLookupKind.Trait => this.BuildFromSheet<Trait>(key, "Name", "Description"),
                TooltipLookupKind.UnknownActionLike => this.BuildFromSheet<LuminaAction>(key, "Name", "Description"),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            this.log.Debug($"[CN Tooltip Overlay] Failed to build source payload for {key.CacheKey}: {ex}");
            return null;
        }
    }

    public static TooltipLookupKey FromHoveredItem(ulong hoveredItem)
    {
        if (hoveredItem == 0)
        {
            return TooltipLookupKey.None;
        }

        var highQuality = hoveredItem > 1_000_000;
        var rowId = highQuality ? hoveredItem - 1_000_000 : hoveredItem;
        if (rowId == 0 || rowId > uint.MaxValue)
        {
            return TooltipLookupKey.None;
        }

        return new TooltipLookupKey(TooltipLookupKind.Item, (uint)rowId, highQuality);
    }

    public static TooltipLookupKey FromHoveredAction(HoveredAction? hoveredAction)
    {
        if (hoveredAction == null || hoveredAction.ActionId == 0)
        {
            return TooltipLookupKey.None;
        }

        var kind = hoveredAction.DetailKind switch
        {
            DetailKind.Action => TooltipLookupKind.Action,
            DetailKind.CraftingAction => TooltipLookupKind.CraftingAction,
            DetailKind.GeneralAction => TooltipLookupKind.GeneralAction,
            DetailKind.Trait => TooltipLookupKind.Trait,
            _ => TooltipLookupKind.UnknownActionLike,
        };

        return new TooltipLookupKey(kind, hoveredAction.ActionId, false, hoveredAction.DetailKind);
    }

    private TooltipPayload? BuildFromSheet<T>(
        TooltipLookupKey key,
        string titleProperty,
        string bodyProperty)
        where T : struct, Lumina.Excel.IExcelRow<T>
    {
        var sheet = this.dataManager.GetExcelSheet<T>(ClientLanguage.English);
        var row = ExcelReflection.GetRowObject(sheet, key.RowId);
        if (ExcelReflection.LooksLikeMissingRow(row, key.RowId) || row == null)
        {
            return null;
        }

        var title = ExcelReflection.ExtractTextProperty(row, titleProperty);
        var body = ExcelReflection.ExtractTextProperty(row, bodyProperty);

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        if (key.HighQuality && !string.IsNullOrWhiteSpace(title) && !title.Contains("", StringComparison.Ordinal))
        {
            title = $"{title} ";
        }

        return new TooltipPayload(key, title, body, string.Empty, string.Empty);
    }
}
