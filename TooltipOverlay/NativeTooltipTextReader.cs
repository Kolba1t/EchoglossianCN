// Copyright (c) fork author. Based on Echoglossian runtime patterns.
// Licensed under the same license terms as your Echoglossian fork.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game.Gui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Echoglossian.TooltipOverlay;

/// <summary>
/// Best-effort reader for the game's already-rendered tooltip addons.
///
/// The Lumina Action/Item sheets often omit client-resolved tooltip details such as
/// "Weaponskill", "Ability", or "Potency: 300". Those details are frequently present
/// in the native ActionDetail/ItemDetail addon after the game builds the tooltip, so this
/// class tries to scrape visible text nodes from that addon and feed that richer text into
/// the existing translation path.
///
/// This is intentionally defensive. If the addon name or node layout changes, it returns
/// null and the overlay falls back to the stable sheet-based payload instead of crashing.
/// </summary>
internal sealed class NativeTooltipTextReader
{
    private static readonly string[] ActionAddonCandidates =
    {
        "ActionDetail",
        "ActionTooltip",
        "ActionHelp",
        "ActionHelpDetail",
    };

    private static readonly string[] ItemAddonCandidates =
    {
        "ItemDetail",
        "ItemDetailCompare",
        "ItemTooltip",
    };

    private readonly IGameGui gameGui;
    private readonly IPluginLog log;

    public NativeTooltipTextReader(IGameGui gameGui, IPluginLog log)
    {
        this.gameGui = gameGui;
        this.log = log;
    }

    public TooltipPayload? TryBuildPayloadFromVisibleTooltip(TooltipLookupKey key, string? preferredTitle)
    {
        if (key.IsNone)
        {
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
            return null;
        }

        var nativeText = this.TryReadFirstVisibleAddonText(addonNames);
        if (string.IsNullOrWhiteSpace(nativeText))
        {
            return null;
        }

        var lines = NormalizeNativeLines(nativeText).ToList();
        if (lines.Count == 0)
        {
            return null;
        }

        var title = PickTitle(lines, preferredTitle);
        var bodyLines = new List<string>();
        var skippedTitle = false;
        foreach (var line in lines)
        {
            if (!skippedTitle && !string.IsNullOrWhiteSpace(title) && SameVisibleText(line, title))
            {
                skippedTitle = true;
                continue;
            }

            bodyLines.Add(line);
        }

        var body = string.Join("\n", bodyLines).Trim();
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        // Avoid replacing a decent sheet payload with a tiny or wrong addon scrape.
        if (!LooksLikeUsefulTooltip(body) && body.Length < 40)
        {
            return null;
        }

        return new TooltipPayload(key, title, body, string.Empty, string.Empty);
    }

    private string TryReadFirstVisibleAddonText(IEnumerable<string> addonNames)
    {
        foreach (var addonName in addonNames)
        {
            try
            {
                var ptr = this.ResolveAddonPointer(addonName);
                if (ptr == nint.Zero)
                {
                    continue;
                }

                var text = this.TryReadAddonTextUnsafe(ptr);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
            catch (Exception ex)
            {
                this.log.Debug($"[CN Tooltip Overlay] Native tooltip scrape failed for {addonName}: {ex.Message}");
            }
        }

        return string.Empty;
    }

    private nint ResolveAddonPointer(string addonName)
    {
        // Use reflection so the patch survives small IGameGui signature differences.
        var methods = this.gameGui.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name == "GetAddonByName")
            .OrderByDescending(m => m.GetParameters().Length)
            .ToArray();

        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            object?[] args;
            if (parameters.Length >= 2)
            {
                args = new object?[parameters.Length];
                args[0] = addonName;
                args[1] = 1;
                for (var i = 2; i < args.Length; i++)
                {
                    args[i] = parameters[i].HasDefaultValue
                        ? parameters[i].DefaultValue
                        : parameters[i].ParameterType.IsValueType
                            ? Activator.CreateInstance(parameters[i].ParameterType)
                            : null;
                }
            }
            else if (parameters.Length == 1)
            {
                args = new object?[] { addonName };
            }
            else
            {
                continue;
            }

            var result = method.Invoke(this.gameGui, args);
            switch (result)
            {
                case IntPtr ip:
                    return ip;
                case nuint nu:
                    return (nint)nu;
                case ulong ul:
                    return (nint)unchecked((long)ul);
                case long l:
                    return (nint)l;
                case uint ui:
                    return (nint)ui;
                case int i:
                    return (nint)i;
            }
        }

        return nint.Zero;
    }

    private unsafe string TryReadAddonTextUnsafe(nint addonPtr)
    {
        var addon = (AtkUnitBase*)addonPtr;
        if (addon == null || !addon->IsVisible)
        {
            return string.Empty;
        }

        var count = (int)addon->UldManager.NodeListCount;
        if (count <= 0 || addon->UldManager.NodeList == null)
        {
            return string.Empty;
        }

        var output = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var max = Math.Min(count, 512);
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

        return output.ToString();
    }

    private static unsafe string ReadTextNode(AtkTextNode* node)
    {
        if (node == null)
        {
            return string.Empty;
        }

        try
        {
            // In current FFXIVClientStructs, NodeText is a CStringPointer value,
            // not a nullable raw pointer. Calling ToString() is the most compatible
            // way to extract the UTF-8 text across Dalamud/API minor revisions.
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
        clean = Regex.Replace(clean, @"\b(Type|Cast|Recast|Range|Radius|Cost|Potency|Combo Potency|Additional Effect|Duration):", "\n$1:", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"\b(Weaponskill|Ability|Spell|Trait)\b", "\n$1", RegexOptions.IgnoreCase);

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
        return Regex.IsMatch(line, @"^(Type|Cast|Recast|Range|Radius|Cost|Potency|Combo Potency|Duration):", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(line, @"^(Weaponskill|Ability|Spell|Trait)$", RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeUsefulTooltip(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        return Regex.IsMatch(body, @"\b(Potency|Combo Potency|Weaponskill|Ability|Spell|Trait|Cast|Recast|Range|Radius|Duration|Additional Effect)\b", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(body, @"\b\d+\s*(?:yalm|yalms|s|sec|seconds|%)\b", RegexOptions.IgnoreCase);
    }

    private static bool SameVisibleText(string a, string b)
    {
        return string.Equals(NormalizeForDedupe(a), NormalizeForDedupe(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeForDedupe(string line)
    {
        return Regex.Replace(line, @"[^\p{L}\p{N}%]+", string.Empty).Trim();
    }
}
