// Copyright (c) fork author. Based on Echoglossian runtime patterns.
// Licensed under the same license terms as your Echoglossian fork.

using System.Reflection;

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
        var prop = row.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (prop == null)
        {
            return string.Empty;
        }

        var value = prop.GetValue(row);
        return ToPlainText(value);
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

        // Lumina SeString-like types usually expose ExtractText(). Prefer it if present.
        var extractText = value.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "ExtractText" && m.GetParameters().Length == 0);
        if (extractText != null)
        {
            return (extractText.Invoke(value, Array.Empty<object?>()) as string ?? string.Empty).Trim();
        }

        // Some generated values are wrappers around string with ToString() implemented.
        return value.ToString()?.Trim() ?? string.Empty;
    }
}
