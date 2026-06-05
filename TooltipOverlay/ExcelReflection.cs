// Copyright (c) fork author. Based on Echoglossian runtime patterns.
// Licensed under the same license terms as your Echoglossian fork.

using System.Reflection;
using System.Text.RegularExpressions;

namespace Echoglossian.TooltipOverlay;

internal static class ExcelReflection
{
    public static object? GetRowObject(object? sheet, uint rowId)
    {
        if (sheet == null)
        {
            return null;
        }

        var sheetType = sheet.GetType();

        // Lumina has moved between GetRow, GetRowOrDefault and TryGetRow-style APIs
        // across versions. Reflection lets this overlay survive small Lumina changes.
        var getRowOrDefault = sheetType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "GetRowOrDefault" && m.GetParameters().Length >= 1);
        if (getRowOrDefault != null)
        {
            return InvokeRowGetter(getRowOrDefault, sheet, rowId);
        }

        var getRow = sheetType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "GetRow" && m.GetParameters().Length >= 1);
        if (getRow != null)
        {
            try
            {
                return InvokeRowGetter(getRow, sheet, rowId);
            }
            catch
            {
                return null;
            }
        }

        var tryGetRow = sheetType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "TryGetRow" && m.GetParameters().Length >= 2);
        if (tryGetRow != null)
        {
            var parameters = tryGetRow.GetParameters();
            var args = new object?[parameters.Length];
            args[0] = ConvertRowId(rowId, parameters[0].ParameterType);
            args[1] = null;
            var ok = tryGetRow.Invoke(sheet, args) as bool?;
            return ok == true ? args[1] : null;
        }

        return null;
    }

    public static string ExtractTextProperty(object row, string propertyName)
    {
        var value = TryGetPropertyValue(row, propertyName);
        return ToPlainText(value);
    }

    public static object? TryGetPropertyValue(object? row, string propertyName)
    {
        if (row == null || string.IsNullOrWhiteSpace(propertyName))
        {
            return null;
        }

        var prop = row.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        return prop?.GetValue(row);
    }

    public static bool TryReadNumber(object? row, string propertyName, out decimal value)
    {
        value = 0;
        var raw = TryGetPropertyValue(row, propertyName);
        if (raw == null)
        {
            return false;
        }

        try
        {
            value = Convert.ToDecimal(raw);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string ExtractBestTextProperty(object row, params string[] propertyNames)
    {
        var best = string.Empty;
        foreach (var propertyName in propertyNames)
        {
            var candidate = ExtractTextProperty(row, propertyName);
            if (IsRicherText(candidate, best))
            {
                best = candidate;
            }
        }

        return best;
    }

    public static bool LooksLikeMissingRow(object? row, uint expectedRowId)
    {
        if (row == null)
        {
            return true;
        }

        var prop = row.GetType().GetProperty("RowId", BindingFlags.Instance | BindingFlags.Public);
        if (prop == null)
        {
            return false;
        }

        try
        {
            var actual = Convert.ToUInt32(prop.GetValue(row));
            return actual != expectedRowId;
        }
        catch
        {
            return false;
        }
    }

    public static string CleanGameText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Replace("\r\n", "\n").Replace('\r', '\n');

        // Strip most private-use UI glyphs/icons before sending text to the translator.
        // FFXIV uses these heavily in tooltips. They can cause Google/OpenAI to return
        // unchanged or malformed output, and the overlay already labels the source kind.
        text = Regex.Replace(text, "[\\uE000-\\uF8FF]", string.Empty);

        // Remove common generated markup while keeping the visible text/numbers.
        text = Regex.Replace(text, @"<[^>]+>", string.Empty);
        text = Regex.Replace(text, @"\\s*\\n\\s*", "\n");
        text = Regex.Replace(text, @"[ \\t]{2,}", " ");
        text = Regex.Replace(text, @"\\n{3,}", "\n\n");

        return text.Trim();
    }

    private static object? InvokeRowGetter(MethodInfo method, object sheet, uint rowId)
    {
        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        args[0] = ConvertRowId(rowId, parameters[0].ParameterType);

        // Current Lumina row getters may include optional subrow/language parameters.
        for (var i = 1; i < args.Length; i++)
        {
            args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
        }

        return method.Invoke(sheet, args);
    }

    private static object ConvertRowId(uint rowId, Type parameterType)
    {
        var type = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
        if (type == typeof(uint)) return rowId;
        if (type == typeof(int)) return unchecked((int)rowId);
        if (type == typeof(ulong)) return (ulong)rowId;
        if (type == typeof(long)) return (long)rowId;
        if (type == typeof(ushort)) return unchecked((ushort)rowId);
        if (type == typeof(short)) return unchecked((short)rowId);
        return rowId;
    }

    private static string ToPlainText(object? value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        var candidates = new List<string>();

        // Lumina SeString-like types usually expose ExtractText(). Prefer it if present.
        var extractText = value.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "ExtractText" && m.GetParameters().Length == 0);
        if (extractText != null)
        {
            candidates.Add(extractText.Invoke(value, Array.Empty<object?>()) as string ?? string.Empty);
        }

        // Some SeString payloads render more numerics in ToString() than ExtractText().
        // Keep ToString() as a fallback, but reject obvious type-name dumps.
        candidates.Add(value.ToString() ?? string.Empty);

        var best = string.Empty;
        foreach (var candidate in candidates.Select(CleanGameText))
        {
            if (IsRicherText(candidate, best))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static bool IsRicherText(string candidate, string current)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (candidate.Contains("Lumina.", StringComparison.Ordinal) ||
            candidate.Contains("SeString", StringComparison.Ordinal) ||
            candidate.Contains("ExcelPage", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(current))
        {
            return true;
        }

        var candidateScore = TextScore(candidate);
        var currentScore = TextScore(current);
        return candidateScore > currentScore;
    }

    private static int TextScore(string text)
    {
        // Reward digits strongly because missing tooltip numbers were the main v5 bug.
        var letters = text.Count(char.IsLetter);
        var digits = text.Count(char.IsDigit);
        var lines = text.Count(c => c == '\n');
        return letters + (digits * 6) + (lines * 8) + Math.Min(text.Length / 8, 80);
    }
}
