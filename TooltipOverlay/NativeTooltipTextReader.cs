// Copyright (c) fork author. Based on Echoglossian runtime patterns.
// Licensed under the same license terms as your Echoglossian fork.

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game.Gui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Echoglossian.TooltipOverlay;

/// <summary>
/// Best-effort reader for the game's already-rendered tooltip addons.
///
/// v13/discovery changes:
/// - skip open generic GetAddonByName overloads that caused the previous
///   "late bound operations" exception;
/// - understand Dalamud native wrapper return types that expose an Address property;
/// - test a wider tooltip addon candidate set and include pointer/text-node diagnostics.
///
/// If this still reports Native chars/lines as 0/0, the next step is to add a real
/// AtkUnitManager-wide addon enumerator. This version is intentionally conservative so it
/// should build against API 15 without relying on unstable custom ClientStructs.
/// </summary>
internal sealed class NativeTooltipTextReader
{
    private static readonly string[] ActionAddonCandidates =
    {
        "ActionDetail",
        "ActionHelp",
        "ActionTooltip",
        "ActionHelpDetail",
        "ActionDetailHelp",
        "ActionHelpInfo",
        "ActionInfo",
        "_ActionDetail",
        "_ActionHelp",
        "_ActionTooltip",
        "_ActionHelpDetail",
        "ItemDetail",        // some hotbar/item actions render through generic detail windows
        "ItemTooltip",
        "Tooltip",
        "Help",
    };

    private static readonly string[] ItemAddonCandidates =
    {
        "ItemDetail",
        "ItemDetailCompare",
        "ItemTooltip",
        "_ItemDetail",
        "_ItemTooltip",
        "ActionDetail",
        "Tooltip",
        "Help",
    };

    private readonly IGameGui gameGui;
    private readonly IPluginLog log;

    public string LastDebugSummary { get; private set; } = "Native tooltip reader has not run yet.";

    public NativeTooltipTextReader(IGameGui gameGui, IPluginLog log)
    {
        this.gameGui = gameGui;
        this.log = log;
    }

    public TooltipPayload? TryBuildPayloadFromVisibleTooltip(TooltipLookupKey key, string? preferredTitle)
    {
        if (key.IsNone)
        {
            this.LastDebugSummary = "Native reader skipped: no key.";
            return null;
        }

        var addonNames = key.Kind switch
        {
            TooltipLookupKind.Item => ItemAddonCandidates,
            TooltipLookupKind.Action => ActionAddonCandidates,
            TooltipLookupKind.CraftingAction => ActionAddonCandidates,
            TooltipLookupKind.GeneralAction => ActionAddonCandidates,
            TooltipLookupKind.Trait => ActionAddonCandidates,
            TooltipLookupKind.UnknownActionLike => ActionAddonCandidates,
            _ => Array.Empty<string>(),
        };

        if (addonNames.Length == 0)
        {
            this.LastDebugSummary = $"Native reader skipped: no addon candidates for {key.Kind}.";
            return null;
        }

        var nativeRead = this.TryReadFirstVisibleAddonText(addonNames);
        var nativeText = nativeRead.Text;
        var debug = new StringBuilder();
        debug.AppendLine($"Candidates: {string.Join(", ", addonNames)}");
        debug.AppendLine($"Selected addon: {(string.IsNullOrWhiteSpace(nativeRead.AddonName) ? "(none)" : nativeRead.AddonName)}");
        debug.AppendLine($"Native chars/lines: {nativeText.Length}/{CountLines(nativeText)}");
        debug.AppendLine("Addon attempts:");
        debug.AppendLine(nativeRead.Attempts);

        if (string.IsNullOrWhiteSpace(nativeText))
        {
            debug.AppendLine("Result: no native text captured.");
            this.LastDebugSummary = debug.ToString().Trim();
            return null;
        }

        var lines = nativeText
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        debug.AppendLine("Captured lines preview:");
        debug.AppendLine(string.Join("\n", lines.Take(18)));

        if (lines.Count == 0)
        {
            debug.AppendLine("Result: native text normalized to zero lines.");
            this.LastDebugSummary = debug.ToString().Trim();
            return null;
        }

        var title = PickTitle(lines, preferredTitle);
        var bodyLines = lines
            .Where(l => !SameVisibleText(l, title))
            .ToList();

        var body = string.Join("\n", bodyLines).Trim();
        debug.AppendLine($"Picked title: {title}");
        debug.AppendLine($"Body chars/lines after title removal: {body.Length}/{CountLines(body)}");
        debug.AppendLine($"Looks useful: {LooksLikeUsefulTooltip(body)}");

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
        {
            debug.AppendLine("Result: no title/body after parsing.");
            this.LastDebugSummary = debug.ToString().Trim();
            return null;
        }

        // Avoid replacing a decent sheet payload with a tiny or wrong addon scrape.
        if (!LooksLikeUsefulTooltip(body) && body.Length < 40)
        {
            debug.AppendLine("Result: rejected because native scrape did not look useful.");
            this.LastDebugSummary = debug.ToString().Trim();
            return null;
        }

        debug.AppendLine("Result: native payload accepted.");
        this.LastDebugSummary = debug.ToString().Trim();
        return new TooltipPayload(key, title, body, string.Empty, string.Empty);
    }

    private NativeReadResult TryReadFirstVisibleAddonText(IEnumerable<string> addonNames)
    {
        var attempts = new StringBuilder();
        foreach (var addonName in addonNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var resolve = this.ResolveAddonPointer(addonName);
                if (resolve.Pointer == nint.Zero)
                {
                    attempts.AppendLine($"{addonName}: missing/null ({resolve.Detail})");
                    continue;
                }

                var textResult = this.TryReadAddonTextUnsafe(resolve.Pointer);
                attempts.AppendLine($"{addonName}: ptr=0x{resolve.Pointer.ToInt64():X}, {resolve.Detail}, visible={textResult.Visible}, nodeCount={textResult.NodeCount}, textNodes={textResult.TextNodes}, chars={textResult.Text.Length}, lines={CountLines(textResult.Text)}");
                if (!string.IsNullOrWhiteSpace(textResult.Text))
                {
                    return new NativeReadResult(textResult.Text, addonName, attempts.ToString().Trim());
                }
            }
            catch (Exception ex)
            {
                attempts.AppendLine($"{addonName}: exception {ex.GetType().Name}: {ex.Message}");
                this.log.Debug($"[CN Tooltip Overlay] Native tooltip scrape failed for {addonName}: {ex}");
            }
        }

        return new NativeReadResult(string.Empty, string.Empty, attempts.ToString().Trim());
    }

    private ResolveResult ResolveAddonPointer(string addonName)
    {
        // Use reflection so this builds across small IGameGui signature differences, but be
        // stricter than v12: never invoke open generic overloads, and understand Dalamud's
        // native wrapper structs with an Address property.
        var methods = this.gameGui.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name == "GetAddonByName" && !m.ContainsGenericParameters)
            .OrderBy(m => m.GetParameters().Length)
            .ToArray();

        var attemptDetails = new List<string>();
        foreach (var method in methods)
        {
            try
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 0 || parameters[0].ParameterType != typeof(string))
                {
                    attemptDetails.Add($"skip {MethodSignature(method)}");
                    continue;
                }

                var args = new object?[parameters.Length];
                args[0] = addonName;
                for (var i = 1; i < parameters.Length; i++)
                {
                    var p = parameters[i];
                    if (p.ParameterType == typeof(int))
                    {
                        args[i] = 1;
                    }
                    else if (p.ParameterType == typeof(uint))
                    {
                        args[i] = 1u;
                    }
                    else if (p.HasDefaultValue)
                    {
                        args[i] = p.DefaultValue;
                    }
                    else
                    {
                        args[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
                    }
                }

                var result = method.Invoke(this.gameGui, args);
                var ptr = PointerFromResult(result, out var detail);
                attemptDetails.Add($"{MethodSignature(method)} => {detail}");
                if (ptr != nint.Zero)
                {
                    return new ResolveResult(ptr, detail);
                }
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                attemptDetails.Add($"{MethodSignature(method)} threw {tie.InnerException.GetType().Name}: {tie.InnerException.Message}");
            }
            catch (Exception ex)
            {
                attemptDetails.Add($"{MethodSignature(method)} threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        return new ResolveResult(nint.Zero, string.Join(" | ", attemptDetails.Take(4)));
    }

    private static string MethodSignature(MethodInfo method)
    {
        var parameters = string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name));
        return $"{method.ReturnType.Name} {method.Name}({parameters})";
    }

    private static nint PointerFromResult(object? result, out string detail)
    {
        if (result == null)
        {
            detail = "null result";
            return nint.Zero;
        }

        switch (result)
        {
            case IntPtr ip:
                detail = $"IntPtr 0x{ip.ToInt64():X}";
                return ip;
            case nuint nu:
                var pNu = (nint)nu;
                detail = $"nuint 0x{pNu.ToInt64():X}";
                return pNu;
            case ulong ul:
                var pUl = (nint)unchecked((long)ul);
                detail = $"ulong 0x{pUl.ToInt64():X}";
                return pUl;
            case long l:
                var pL = (nint)l;
                detail = $"long 0x{pL.ToInt64():X}";
                return pL;
            case uint ui:
                var pUi = (nint)ui;
                detail = $"uint 0x{pUi.ToInt64():X}";
                return pUi;
            case int i:
                var pI = (nint)i;
                detail = $"int 0x{pI.ToInt64():X}";
                return pI;
        }

        var type = result.GetType();
        foreach (var memberName in new[] { "Address", "Pointer", "Ptr", "Value" })
        {
            var prop = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null && prop.GetIndexParameters().Length == 0)
            {
                try
                {
                    var value = prop.GetValue(result);
                    var ptr = PointerFromResult(value, out var subDetail);
                    detail = $"{type.Name}.{memberName} => {subDetail}";
                    return ptr;
                }
                catch
                {
                    // Try the next member.
                }
            }

            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try
                {
                    var value = field.GetValue(result);
                    var ptr = PointerFromResult(value, out var subDetail);
                    detail = $"{type.Name}.{memberName} field => {subDetail}";
                    return ptr;
                }
                catch
                {
                    // Try the next member.
                }
            }
        }

        var asString = result.ToString() ?? string.Empty;
        var hex = Regex.Match(asString, @"0x(?<hex>[0-9A-Fa-f]+)");
        if (hex.Success && long.TryParse(hex.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            var ptr = (nint)parsed;
            detail = $"{type.Name}.ToString parsed 0x{ptr.ToInt64():X}";
            return ptr;
        }

        detail = $"unsupported return type {type.FullName}: {asString}";
        return nint.Zero;
    }

    private unsafe AddonTextResult TryReadAddonTextUnsafe(nint addonPtr)
    {
        var addon = (AtkUnitBase*)addonPtr;
        if (addon == null)
        {
            return new AddonTextResult(string.Empty, false, 0, 0);
        }

        var visible = addon->IsVisible;
        if (!visible)
        {
            return new AddonTextResult(string.Empty, false, 0, 0);
        }

        var count = (int)addon->UldManager.NodeListCount;
        if (count <= 0 || addon->UldManager.NodeList == null)
        {
            return new AddonTextResult(string.Empty, true, Math.Max(0, count), 0);
        }

        var output = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var textNodes = 0;
        var max = Math.Min(count, 1024);
        for (var i = 0; i < max; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || !node->IsVisible())
            {
                continue;
            }

            if (node->Type != NodeType.Text)
            {
                continue;
            }

            textNodes++;
            var textNode = (AtkTextNode*)node;
            var text = ReadTextNode(textNode);
            text = ExcelReflection.CleanGameText(text);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            foreach (var line in NormalizeNativeLines(text))
            {
                var key = NormalizeForDedupe(line);
                if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
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

        return new AddonTextResult(output.ToString(), true, count, textNodes);
    }

    private static unsafe string ReadTextNode(AtkTextNode* node)
    {
        if (node == null)
        {
            return string.Empty;
        }

        try
        {
            return node->NodeText.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IEnumerable<string> NormalizeNativeLines(string text)
    {
        var clean = ExcelReflection.CleanGameText(text);
        clean = Regex.Replace(clean, @"\s+", " ");

        // Put common tooltip labels on their own lines if the native node joined them.
        clean = Regex.Replace(clean, @"\b(Type|Cast|Recast|Range|Radius|Cost|Potency|Combo Potency|Additional Effect|Duration|Acquired|Category):", "\n$1:", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"\b(Weaponskill|Ability|Spell|Trait)", "\n$1", RegexOptions.IgnoreCase);

        foreach (var raw in clean.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.Length == 1 && !char.IsLetterOrDigit(line[0]))
            {
                continue;
            }

            yield return line;
        }
    }

    private static string PickTitle(IReadOnlyList<string> lines, string? preferredTitle)
    {
        if (!string.IsNullOrWhiteSpace(preferredTitle))
        {
            var preferred = preferredTitle.Trim();
            var exact = lines.FirstOrDefault(l => SameVisibleText(l, preferred));
            if (!string.IsNullOrWhiteSpace(exact))
            {
                return exact;
            }

            return preferred;
        }

        return lines.FirstOrDefault(l => !LooksLikeTooltipMetadata(l)) ?? string.Empty;
    }

    private static bool LooksLikeTooltipMetadata(string line)
    {
        return Regex.IsMatch(line, @"^(Type|Cast|Recast|Range|Radius|Cost|Potency|Combo Potency|Duration|Acquired|Category):", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(line, @"^(Weaponskill|Ability|Spell|Trait)$", RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeUsefulTooltip(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        return Regex.IsMatch(body, @"\b(Potency|Combo Potency|Weaponskill|Ability|Spell|Trait|Cast|Recast|Range|Radius|Duration|Additional Effect|Grants|Delivers|Deals)\b", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(body, @"\b\d+\s*(?:yalm|yalms|s|sec|seconds|%)\b", RegexOptions.IgnoreCase);
    }

    private static bool SameVisibleText(string a, string b)
    {
        return string.Equals(NormalizeForDedupe(a), NormalizeForDedupe(b), StringComparison.OrdinalIgnoreCase);
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Count(l => !string.IsNullOrWhiteSpace(l));
    }

    private readonly struct NativeReadResult
    {
        public NativeReadResult(string text, string addonName, string attempts)
        {
            this.Text = text;
            this.AddonName = addonName;
            this.Attempts = attempts;
        }

        public string Text { get; }
        public string AddonName { get; }
        public string Attempts { get; }
    }

    private readonly struct ResolveResult
    {
        public ResolveResult(nint pointer, string detail)
        {
            this.Pointer = pointer;
            this.Detail = detail;
        }

        public nint Pointer { get; }
        public string Detail { get; }
    }

    private readonly struct AddonTextResult
    {
        public AddonTextResult(string text, bool visible, int nodeCount, int textNodes)
        {
            this.Text = text;
            this.Visible = visible;
            this.NodeCount = nodeCount;
            this.TextNodes = textNodes;
        }

        public string Text { get; }
        public bool Visible { get; }
        public int NodeCount { get; }
        public int TextNodes { get; }
    }

    private static string NormalizeForDedupe(string line)
    {
        return Regex.Replace(line, @"[^\p{L}\p{N}%]+", string.Empty).Trim();
    }
}
