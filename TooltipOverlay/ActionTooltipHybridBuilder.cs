// Copyright (c) fork author. Based on Echoglossian runtime patterns.
// Licensed under the same license terms as your Echoglossian fork.

using System.Text;
using System.Text.RegularExpressions;

namespace Echoglossian.TooltipOverlay;

/// <summary>
/// Builds a clean, action-ID keyed tooltip payload from structured sheet data plus the
/// already-rendered native tooltip. The native tooltip is no longer displayed directly:
/// it is only used to recover resolved values such as potency, type, range, and radius.
/// This avoids the private FFXIV UI glyphs that produced the placeholder bar/equals signs.
/// </summary>
internal static class ActionTooltipHybridBuilder
{
    public static TooltipPayload? TryBuild(
        TooltipLookupKey key,
        TooltipPayload? sheetPayload,
        TooltipPayload? nativePayload,
        StringBuilder? debug = null)
    {
        if (key.IsNone || (sheetPayload == null && nativePayload == null))
        {
            return null;
        }

        var nativeText = CleanNativeTooltipText(
            (nativePayload?.OriginalTitle ?? string.Empty) + "\n" + (nativePayload?.OriginalBody ?? string.Empty));
        var nativeLines = SplitTooltipLines(nativeText).ToList();

        var sheetText = CleanSheetText(
            (sheetPayload?.OriginalTitle ?? string.Empty) + "\n" + (sheetPayload?.OriginalBody ?? string.Empty));
        var sheetLines = SplitTooltipLines(sheetText).ToList();

        var title = FirstNonEmpty(sheetPayload?.OriginalTitle, nativePayload?.OriginalTitle, $"Action #{key.RowId}");
        title = StripActionIdSuffix(CleanNativeTooltipText(title)).Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = $"Action #{key.RowId}";
        }

        var actionType = ExtractActionType(nativeLines) ?? ExtractActionType(sheetLines);
        var job = ExtractJob(nativeLines) ?? ExtractJob(sheetLines);
        var category = ExtractCategory(nativeLines) ?? ExtractCategory(sheetLines);
        var level = ExtractLevel(nativeLines) ?? ExtractLabeledValue(sheetLines, "Level") ?? ExtractLabeledValue(sheetLines, "Lv");

        var descriptionLines = ExtractDescriptionLines(nativeLines);
        if (descriptionLines.Count == 0)
        {
            descriptionLines = ExtractDescriptionLines(sheetLines);
        }

        var description = string.Join("\n", descriptionLines).Trim();
        var potency = ExtractPotency(description) ?? ExtractPotency(nativeText) ?? ExtractPotency(sheetText);

        var cast = ExtractTimeValue(nativeLines, "Cast") ?? ExtractLabeledValue(sheetLines, "Cast time") ?? ExtractLabeledValue(sheetLines, "Cast");
        var recast = ExtractTimeValue(nativeLines, "Recast") ?? ExtractLabeledValue(sheetLines, "Recast time") ?? ExtractLabeledValue(sheetLines, "Recast");
        var range = ExtractYalmValue(nativeLines, "Range") ?? ExtractLabeledValue(sheetLines, "Range");
        var radius = ExtractYalmValue(nativeLines, "Radius") ?? ExtractLabeledValue(sheetLines, "Radius");
        var mpCost = ExtractCost(nativeLines) ?? ExtractCost(sheetLines);

        var body = new StringBuilder();
        AddLine(body, "Skill type", actionType);
        AddLine(body, "Job", job);
        AddLine(body, "Category", category);
        AddLine(body, "Level", level);
        AddLine(body, "Potency", potency);

        if (!string.IsNullOrWhiteSpace(description))
        {
            if (body.Length > 0)
            {
                body.AppendLine();
            }

            body.AppendLine("Description:");
            body.AppendLine(description.Trim());
        }

        var dataLines = new List<string>();
        AddLine(dataLines, "Cast time", NormalizeInstant(cast));
        AddLine(dataLines, "Recast time", recast);
        AddLine(dataLines, "Range", NormalizeYalms(range));
        AddLine(dataLines, "Radius", NormalizeYalms(radius));
        AddLine(dataLines, "MP cost", mpCost);

        if (dataLines.Count > 0)
        {
            if (body.Length > 0)
            {
                body.AppendLine();
            }

            body.AppendLine("Action data:");
            foreach (var line in dataLines)
            {
                body.AppendLine(line);
            }
        }

        var finalBody = body.ToString().Trim();
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(finalBody))
        {
            return null;
        }

        debug?.AppendLine("Hybrid action builder:");
        debug?.AppendLine($"  native lines={nativeLines.Count}, sheet lines={sheetLines.Count}");
        debug?.AppendLine($"  type={actionType ?? "(none)"}, potency={potency ?? "(none)"}, range={range ?? "(none)"}, radius={radius ?? "(none)"}");
        debug?.AppendLine($"  description lines={descriptionLines.Count}");
        debug?.AppendLine("Selected source: ActionIdHybrid(native values + clean sheet/action text)");

        return new TooltipPayload(key, title, finalBody, string.Empty, string.Empty);
    }

    public static string CleanNativeTooltipText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var clean = ExcelReflection.CleanGameText(text);
        var sb = new StringBuilder(clean.Length);
        foreach (var c in clean)
        {
            // Keep only normal printable text that can be safely sent to translation.
            // FFXIV private UI/color/icon glyphs are intentionally replaced with spaces.
            if (c == '\r' || c == '\n' || c == '\t' || (c >= 0x20 && c <= 0x7E) || (c >= '\u3400' && c <= '\u9FFF'))
            {
                sb.Append(c);
            }
            else
            {
                sb.Append(' ');
            }
        }

        clean = sb.ToString();
        clean = Regex.Replace(clean, @"[═=]{2,}", " ");
        clean = Regex.Replace(clean, @"[ \t]{2,}", " ");
        clean = Regex.Replace(clean, @"\n{3,}", "\n\n");
        return clean.Trim();
    }

    private static string CleanSheetText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var clean = ExcelReflection.CleanGameText(text);
        clean = Regex.Replace(clean, @"[═=]{2,}", " ");
        clean = Regex.Replace(clean, @"[ \t]{2,}", " ");
        clean = Regex.Replace(clean, @"\n{3,}", "\n\n");
        return clean.Trim();
    }

    private static IEnumerable<string> SplitTooltipLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var clean = text.Replace("\r\n", "\n").Replace('\r', '\n');
        clean = Regex.Replace(clean, @"(?<=\.)\s+(?=(Deals|Delivers|Restores|Grants|Additional Effect|Combo Bonus|Duration|Can only|This action|Upon execution|When standing|Consumes|Increases|Reduces|Extends)\b)", "\n", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"\b(Additional Effect|Combo Bonus|Duration|Can only|This action|Upon execution|When standing|Consumes|Range|Radius|Cast|Recast|MP Cost|Cost|Potency):", "\n$1:", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"\b(Weaponskill|Ability|Spell|Trait)\s*\[(\d+)\]", "\n$1 [$2]", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"\b(Weaponskill|Ability|Spell|Trait)\b", "\n$1", RegexOptions.IgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in clean.Split('\n'))
        {
            var line = Regex.Replace(raw.Trim(), @"\s+", " ").Trim();
            line = StripActionIdSuffix(line).Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var key = NormalizeForDedupe(line);
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
            {
                continue;
            }

            yield return line;
        }
    }

    private static List<string> ExtractDescriptionLines(IReadOnlyList<string> lines)
    {
        var output = new List<string>();
        foreach (var line in lines)
        {
            if (LooksLikeMetadataOnly(line))
            {
                continue;
            }

            if (LooksLikeDescriptionLine(line))
            {
                output.Add(NormalizeDescriptionLine(line));
            }
        }

        return output.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool LooksLikeDescriptionLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (Regex.IsMatch(line, @"\b(Deals|Delivers|Restores|Grants|Additional Effect|Combo Bonus|Duration|Can only|This action|Upon execution|When standing|Consumes|Increases|Reduces|Extends|potency|effect|recast timer|weaponskills|magic actions)\b", RegexOptions.IgnoreCase))
        {
            return true;
        }

        var letters = line.Count(char.IsLetter);
        return line.Length >= 35 && letters >= 20 && !LooksLikeMetadataOnly(line);
    }

    private static bool LooksLikeMetadataOnly(string line)
    {
        var trimmed = StripActionIdSuffix(line.Trim());
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return true;
        }

        if (Regex.IsMatch(trimmed, @"^(PCT|PLD|WAR|DRK|GNB|WHM|SCH|AST|SGE|MNK|DRG|NIN|SAM|RPR|VPR|BRD|MCH|DNC|BLM|SMN|RDM|BLU|CRP|BSM|ARM|GSM|LTW|WVR|ALC|CUL|MIN|BTN|FSH)$", RegexOptions.IgnoreCase))
        {
            return true;
        }

        return Regex.IsMatch(trimmed, @"^(Affinity|Acquired|Action data|Additional data|Description|Lv\.?\s*\d+|Level\s*:?\s*\d+|Skill type\s*:?.*|Job\s*:?.*|Category\s*:?.*|Potency\s*:?.*|Cast\s*:?.*|Recast\s*:?.*|Cast time\s*:?.*|Recast time\s*:?.*|Range\s*:?.*|Radius\s*:?.*|MP Cost\s*:?.*|Cost\s*:?.*|\d+(?:\.\d+)?\s*y|\d+(?:\.\d+)?\s*yalms?|Weaponskill|Ability|Spell|Trait)$", RegexOptions.IgnoreCase);
    }

    private static string NormalizeDescriptionLine(string line)
    {
        var clean = line.Trim();
        clean = Regex.Replace(clean, @"\s+", " ");
        clean = Regex.Replace(clean, @"\s+([,.;:])", "$1");
        return clean.Trim();
    }

    private static string? ExtractActionType(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            var match = Regex.Match(line, @"\b(Weaponskill|Ability|Spell|Trait)\b", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return ToTitle(match.Groups[1].Value);
            }
        }

        return null;
    }

    private static string? ExtractJob(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (Regex.IsMatch(trimmed, @"^(PCT|PLD|WAR|DRK|GNB|WHM|SCH|AST|SGE|MNK|DRG|NIN|SAM|RPR|VPR|BRD|MCH|DNC|BLM|SMN|RDM|BLU|CRP|BSM|ARM|GSM|LTW|WVR|ALC|CUL|MIN|BTN|FSH)$", RegexOptions.IgnoreCase))
            {
                return trimmed.ToUpperInvariant();
            }
        }

        return null;
    }

    private static string? ExtractCategory(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Equals("Affinity", StringComparison.OrdinalIgnoreCase))
            {
                return "Affinity";
            }
        }

        return null;
    }

    private static string? ExtractLevel(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            var match = Regex.Match(line, @"\bLv\.?\s*(?<value>\d+)\b|\bLevel\s*:?\s*(?<value2>\d+)\b", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return FirstNonEmpty(match.Groups["value"].Value, match.Groups["value2"].Value);
            }
        }

        return null;
    }

    private static string? ExtractPotency(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Regex.Match(text, @"\bpotency\s+(?:of\s+)?(?<value>\d{1,5})\b|\bPotency\s*:?\s*(?<value2>\d{1,5})\b", RegexOptions.IgnoreCase);
        return match.Success ? FirstNonEmpty(match.Groups["value"].Value, match.Groups["value2"].Value) : null;
    }

    private static string? ExtractTimeValue(IReadOnlyList<string> lines, string label)
    {
        var labelPattern = Regex.Escape(label);
        foreach (var line in lines)
        {
            var match = Regex.Match(line, $@"\b{labelPattern}\b\s*:?\s*(?<value>Instant|\d+(?:\.\d+)?\s*s|\d+(?:\.\d+)?\s*sec(?:onds?)?)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups["value"].Value.Trim();
            }
        }

        return null;
    }

    private static string? ExtractYalmValue(IReadOnlyList<string> lines, string label)
    {
        var labelPattern = Regex.Escape(label);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var direct = Regex.Match(line, $@"\b{labelPattern}\b\s*:?\s*(?<value>\d+(?:\.\d+)?)\s*y(?:alms?)?\b", RegexOptions.IgnoreCase);
            if (direct.Success)
            {
                return direct.Groups["value"].Value + " yalms";
            }

            var reverse = Regex.Match(line, $@"(?<value>\d+(?:\.\d+)?)\s*y\s*\b{labelPattern}\b", RegexOptions.IgnoreCase);
            if (reverse.Success)
            {
                return reverse.Groups["value"].Value + " yalms";
            }

            if (line.Equals(label, StringComparison.OrdinalIgnoreCase) && i > 0)
            {
                var prev = Regex.Match(lines[i - 1], @"^(?<value>\d+(?:\.\d+)?)\s*y$", RegexOptions.IgnoreCase);
                if (prev.Success)
                {
                    return prev.Groups["value"].Value + " yalms";
                }
            }

            if (Regex.IsMatch(line, @"^\d+(?:\.\d+)?\s*y$", RegexOptions.IgnoreCase) && i + 1 < lines.Count && lines[i + 1].Equals(label, StringComparison.OrdinalIgnoreCase))
            {
                var value = Regex.Match(line, @"\d+(?:\.\d+)?").Value;
                return value + " yalms";
            }
        }

        return null;
    }

    private static string? ExtractCost(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            var match = Regex.Match(line, @"\bMP\s*Cost\b\s*:?\s*(?<value>\d+)|\bCost\b\s*:?\s*(?<value2>\d+)\s*MP", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var value = FirstNonEmpty(match.Groups["value"].Value, match.Groups["value2"].Value);
                return string.IsNullOrWhiteSpace(value) ? null : value + " MP";
            }
        }

        return null;
    }

    private static string? ExtractLabeledValue(IReadOnlyList<string> lines, string label)
    {
        var labelPattern = Regex.Escape(label);
        foreach (var line in lines)
        {
            var match = Regex.Match(line, $@"^\s*{labelPattern}\s*:?\s*(?<value>.+?)\s*$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var value = match.Groups["value"].Value.Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    private static void AddLine(StringBuilder body, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        body.AppendLine($"{label}: {value.Trim()}");
    }

    private static void AddLine(ICollection<string> lines, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        lines.Add($"{label}: {value.Trim()}");
    }

    private static string? NormalizeInstant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().Equals("0s", StringComparison.OrdinalIgnoreCase) ? "Instant" : value.Trim();
    }

    private static string? NormalizeYalms(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value, @"(?<value>\d+(?:\.\d+)?)");
        return match.Success ? match.Groups["value"].Value + " yalms" : value.Trim();
    }

    private static string StripActionIdSuffix(string value)
    {
        return Regex.Replace(value ?? string.Empty, @"\s*\[\d{1,6}\]\s*$", string.Empty).Trim();
    }

    private static string ToTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var lower = value.ToLowerInvariant();
        return char.ToUpperInvariant(lower[0]) + lower[1..];
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string NormalizeForDedupe(string line)
    {
        return Regex.Replace(line, @"[^A-Za-z0-9%]+", string.Empty).Trim();
    }
}
