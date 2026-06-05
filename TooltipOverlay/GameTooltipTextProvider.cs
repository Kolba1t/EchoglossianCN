// Copyright (c) fork author. Based on Echoglossian runtime patterns.
// Licensed under the same license terms as your Echoglossian fork.

using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Game.Gui;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaTrait = Lumina.Excel.Sheets.Trait;
using LuminaActionTransient = Lumina.Excel.Sheets.ActionTransient;
using LuminaActionCategory = Lumina.Excel.Sheets.ActionCategory;

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
                TooltipLookupKind.CraftingAction => this.BuildCraftActionPayload(key),
                TooltipLookupKind.GeneralAction => this.BuildFromSheet<GeneralAction>(key, "Name", "Description"),
                TooltipLookupKind.Trait => this.BuildTraitPayload(key),
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
        var sheet = this.dataManager.GetExcelSheet<LuminaAction>(ClientLanguage.English);
        var row = ExcelReflection.GetRowObject(sheet, key.RowId);
        if (row == null || ExcelReflection.LooksLikeMissingRow(row, key.RowId))
        {
            return null;
        }

        var title = ExcelReflection.ExtractBestTextProperty(row, "Name");
        var body = ExcelReflection.ExtractBestTextProperty(row, "Description");

        // Many newer action tooltip lines live in ActionTransient or carry more resolved
        // numbers there than in Action.Description. Merge rather than replacing so we keep
        // both descriptive text and numeric lines when one source is incomplete.
        var transient = this.TryReadSheetText<LuminaActionTransient>(key.RowId, "Description", "DescriptionShort", "Text", "Tooltip");
        body = MergeTooltipSections(body, transient);

        var supplemental = this.BuildActionSupplement(row, body, transient);
        body = MergeTooltipSections(body, supplemental);

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        return new TooltipPayload(key, title, body, string.Empty, string.Empty);
    }

    private TooltipPayload? BuildCraftActionPayload(TooltipLookupKey key)
    {
        var payload = this.BuildFromSheet<CraftAction>(key, "Name", "Description");
        if (payload == null)
        {
            return null;
        }

        var sheet = this.dataManager.GetExcelSheet<CraftAction>(ClientLanguage.English);
        var row = ExcelReflection.GetRowObject(sheet, key.RowId);
        if (row != null && !ExcelReflection.LooksLikeMissingRow(row, key.RowId))
        {
            var supplemental = this.BuildGenericSupplement(row);
            payload = payload with { OriginalBody = MergeTooltipSections(payload.OriginalBody, supplemental) };
        }

        return payload;
    }

    private TooltipPayload? BuildTraitPayload(TooltipLookupKey key)
    {
        var payload = this.BuildFromSheet<LuminaTrait>(key, "Name", "Description");
        if (payload == null)
        {
            return null;
        }

        var sheet = this.dataManager.GetExcelSheet<LuminaTrait>(ClientLanguage.English);
        var row = ExcelReflection.GetRowObject(sheet, key.RowId);
        if (row != null && !ExcelReflection.LooksLikeMissingRow(row, key.RowId))
        {
            var supplemental = this.BuildGenericSupplement(row);
            payload = payload with { OriginalBody = MergeTooltipSections(payload.OriginalBody, supplemental) };
        }

        return payload;
    }

    private string TryReadSheetText<T>(uint rowId, params string[] propertyNames)
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

            return ExcelReflection.ExtractBestTextProperty(row, propertyNames);
        }
        catch (Exception ex)
        {
            this.log.Debug($"[CN Tooltip Overlay] Failed to read {typeof(T).Name} row {rowId}: {ex.Message}");
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

        var title = ExcelReflection.ExtractBestTextProperty(row, titleProperty);
        var body = ExcelReflection.ExtractBestTextProperty(row, bodyProperty);

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

    private string BuildActionSupplement(object row, string description, string transientDescription)
    {
        var lines = new List<string>();

        AddActionType(lines, row);
        AddPotency(lines, row, description, transientDescription);
        AddLevel(lines, row);
        AddHundredMs(lines, row, "Cast100ms", "Cast time", zeroAsInstant: true);
        AddHundredMs(lines, row, "Recast100ms", "Recast time", zeroAsInstant: false);
        AddRange(lines, row, "Range", "Range", meleeFallbackYalms: 3);
        AddRange(lines, row, "EffectRange", "Radius", meleeFallbackYalms: null);
        AddNumber(lines, row, "XAxisModifier", "Width/axis modifier", skipZero: true);
        AddNumber(lines, row, "MaxCharges", "Maximum charges", skipZero: true);
        AddCost(lines, row);

        return lines.Count == 0
            ? string.Empty
            : "Action data:\n" + string.Join("\n", lines);
    }

    private string BuildGenericSupplement(object row)
    {
        var lines = new List<string>();
        AddLevel(lines, row);
        AddNumber(lines, row, "Cost", "Cost", skipZero: true);
        AddNumber(lines, row, "CP", "CP", skipZero: true);
        AddNumber(lines, row, "GP", "GP", skipZero: true);
        return lines.Count == 0
            ? string.Empty
            : "Additional data:\n" + string.Join("\n", lines);
    }

    private void AddActionType(ICollection<string> lines, object row)
    {
        var label = ResolveActionCategory(row);
        if (!string.IsNullOrWhiteSpace(label))
        {
            lines.Add($"Skill type: {label}");
        }
    }

    private string ResolveActionCategory(object row)
    {
        var raw = ExcelReflection.TryGetPropertyValue(row, "ActionCategory");
        var categoryName = ExcelReflection.ExtractReferenceText(raw, "Name", "Text", "Category");
        if (!string.IsNullOrWhiteSpace(categoryName))
        {
            return categoryName;
        }

        if (ExcelReflection.TryExtractReferenceRowId(raw, out var rowId))
        {
            // Current Lumina action-category IDs are commonly: 2 = Spell,
            // 3 = Weaponskill, 4 = Ability. Keep this as a fallback only; if Lumina
            // exposes the ActionCategory row name, that wins.
            var fallback = rowId switch
            {
                2 => "Spell",
                3 => "Weaponskill",
                4 => "Ability",
                _ => string.Empty,
            };

            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return fallback;
            }

            try
            {
                var sheet = this.dataManager.GetExcelSheet<LuminaActionCategory>(ClientLanguage.English);
                var categoryRow = ExcelReflection.GetRowObject(sheet, rowId);
                return ExcelReflection.ExtractBestTextProperty(categoryRow!, "Name", "Text", "Category");
            }
            catch (Exception ex)
            {
                this.log.Debug($"[CN Tooltip Overlay] Failed to resolve ActionCategory {rowId}: {ex.Message}");
            }
        }

        return string.Empty;
    }

    private static void AddPotency(ICollection<string> lines, object row, params string[] sourceText)
    {
        var potencyValues = new List<decimal>();

        foreach (var text in sourceText)
        {
            foreach (var potency in ExtractPotenciesFromText(text))
            {
                potencyValues.Add(potency);
            }
        }

        // Some schemas or future sheets may expose potency-like numeric columns directly.
        // This is intentionally conservative: only property names that explicitly contain
        // potency are accepted, so we do not accidentally label cooldown/range IDs as damage.
        foreach (var potency in ExcelReflection.ReadNumbersFromPropertiesNamedLike(row, "Potency"))
        {
            if (potency > 0)
            {
                potencyValues.Add(potency);
            }
        }

        var unique = potencyValues
            .Where(v => v > 0)
            .Distinct()
            .Take(6)
            .ToList();

        if (unique.Count == 1)
        {
            lines.Add($"Potency: {FormatNumber(unique[0])}");
        }
        else if (unique.Count > 1)
        {
            lines.Add("Potency values: " + string.Join(", ", unique.Select(FormatNumber)));
        }
    }

    private static IEnumerable<decimal> ExtractPotenciesFromText(string? text)
    {
        var clean = ExcelReflection.CleanGameText(text);
        if (string.IsNullOrWhiteSpace(clean))
        {
            yield break;
        }

        // Examples this catches:
        // "Delivers an attack with a potency of 220."
        // "Potency: 400"
        // "... 100 potency ..."
        foreach (Match match in Regex.Matches(clean, @"(?i)(?:potency\s*(?:of|:)\s*)(?<n>\d{1,5}(?:\.\d+)?)|(?<n>\d{1,5}(?:\.\d+)?)\s*potency"))
        {
            if (decimal.TryParse(match.Groups["n"].Value, out var value) && value > 0)
            {
                yield return value;
            }
        }
    }

    private static void AddLevel(ICollection<string> lines, object row)
    {
        if (ExcelReflection.TryReadNumber(row, "ClassJobLevel", out var level) && level > 0)
        {
            lines.Add($"Level: {FormatNumber(level)}");
        }
    }

    private static void AddCost(ICollection<string> lines, object row)
    {
        if (ExcelReflection.TryReadNumber(row, "PrimaryCostValue", out var primaryCost) && primaryCost > 0)
        {
            var label = "Primary cost";
            var type = ExcelReflection.TryGetPropertyValue(row, "PrimaryCostType")?.ToString();
            if (!string.IsNullOrWhiteSpace(type) && type != "0")
            {
                label = $"Primary cost ({type})";
            }

            lines.Add($"{label}: {FormatNumber(primaryCost)}");
        }

        if (ExcelReflection.TryReadNumber(row, "SecondaryCostValue", out var secondaryCost) && secondaryCost > 0)
        {
            var label = "Secondary cost";
            var type = ExcelReflection.TryGetPropertyValue(row, "SecondaryCostType")?.ToString();
            if (!string.IsNullOrWhiteSpace(type) && type != "0")
            {
                label = $"Secondary cost ({type})";
            }

            lines.Add($"{label}: {FormatNumber(secondaryCost)}");
        }
    }

    private static void AddHundredMs(ICollection<string> lines, object row, string propertyName, string label, bool zeroAsInstant)
    {
        if (!ExcelReflection.TryReadNumber(row, propertyName, out var value))
        {
            return;
        }

        if (value <= 0)
        {
            if (zeroAsInstant)
            {
                lines.Add($"{label}: Instant");
            }

            return;
        }

        lines.Add($"{label}: {FormatNumber(value / 10m)}s");
    }

    private static void AddNumber(ICollection<string> lines, object row, string propertyName, string label, bool skipZero)
    {
        if (!ExcelReflection.TryReadNumber(row, propertyName, out var value))
        {
            return;
        }

        if (skipZero && value == 0)
        {
            return;
        }

        // Negative values usually mean "not applicable" or client-resolved display data.
        // Do not show raw -1 style values in the overlay unless a dedicated helper maps them.
        if (value < 0)
        {
            return;
        }

        lines.Add($"{label}: {FormatNumber(value)}");
    }

    private static void AddRange(ICollection<string> lines, object row, string propertyName, string label, decimal? meleeFallbackYalms)
    {
        if (!ExcelReflection.TryReadNumber(row, propertyName, out var value))
        {
            return;
        }

        if (value == 0)
        {
            return;
        }

        // Some melee weaponskills/actions expose Range = -1 in the sheet while the native
        // tooltip resolves that to the normal melee range. Avoid showing raw -1 to users.
        if (value < 0)
        {
            if (meleeFallbackYalms.HasValue)
            {
                lines.Add($"{label}: {FormatNumber(meleeFallbackYalms.Value)} yalms");
            }

            return;
        }

        lines.Add($"{label}: {FormatNumber(value)} yalms");
    }

    private static string MergeTooltipSections(params string[] sections)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new StringBuilder();

        foreach (var section in sections)
        {
            var clean = ExcelReflection.CleanGameText(section);
            if (string.IsNullOrWhiteSpace(clean))
            {
                continue;
            }

            foreach (var rawLine in clean.Split('\n'))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var key = NormalizeForDedupe(line);
                if (!seen.Add(key))
                {
                    continue;
                }

                if (output.Length > 0)
                {
                    output.AppendLine();
                }

                output.Append(line);
            }
        }

        return output.ToString();
    }

    private static string NormalizeForDedupe(string line)
    {
        return new string(line.Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    private static string FormatNumber(decimal number)
    {
        return number % 1 == 0
            ? number.ToString("0")
            : number.ToString("0.##");
    }
}
