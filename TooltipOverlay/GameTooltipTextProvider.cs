// Copyright (c) fork author. Based on Echoglossian runtime patterns.
// Licensed under the same license terms as your Echoglossian fork.

using Dalamud.Game;
using Dalamud.Game.Gui;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaTrait = Lumina.Excel.Sheets.Trait;
using LuminaActionTransient = Lumina.Excel.Sheets.ActionTransient;

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
                TooltipLookupKind.Action => this.BuildActionPayload(key),
                TooltipLookupKind.CraftingAction => this.BuildFromSheet<CraftAction>(key, "Name", "Description"),
                TooltipLookupKind.GeneralAction => this.BuildFromSheet<GeneralAction>(key, "Name", "Description"),
                TooltipLookupKind.Trait => this.BuildFromSheet<LuminaTrait>(key, "Name", "Description"),
                TooltipLookupKind.UnknownActionLike => this.BuildActionPayload(key),
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

    private TooltipPayload? BuildActionPayload(TooltipLookupKey key)
    {
        var payload = this.BuildFromSheet<LuminaAction>(key, "Name", "Description");
        if (payload == null)
        {
            return null;
        }

        // In many current sheets the rendered action tooltip body is richer than
        // Action.Description, and some useful action text is stored in ActionTransient.
        // This is still not a perfect clone of the native tooltip, but it recovers many
        // skill descriptions that the first overlay build showed as name-only.
        var transientDescription = this.TryReadSheetText<LuminaActionTransient>(key.RowId, "Description");
        if (!string.IsNullOrWhiteSpace(transientDescription) &&
            transientDescription.Length > payload.OriginalBody.Length)
        {
            payload = payload with { OriginalBody = transientDescription };
        }

        return payload;
    }

    private string TryReadSheetText<T>(uint rowId, string propertyName)
        where T : struct, Lumina.Excel.IExcelRow<T>
    {
        try
        {
            var sheet = this.dataManager.GetExcelSheet<T>(ClientLanguage.English);
            var row = ExcelReflection.GetRowObject(sheet, rowId);
            if (row == null || ExcelReflection.LooksLikeMissingRow(row, rowId))
            {
                return string.Empty;
            }

            return ExcelReflection.ExtractTextProperty(row, propertyName);
        }
        catch (Exception ex)
        {
            this.log.Debug($"[CN Tooltip Overlay] Failed to read {typeof(T).Name}.{propertyName} row {rowId}: {ex.Message}");
            return string.Empty;
        }
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
