using System.Text.RegularExpressions;

namespace Echoglossian.TooltipOverlay;

/// <summary>
/// Conservative display-time cleanup for common FFXIV action tooltip fragments that
/// sometimes survive the structured tooltip parser/translator in English or mixed form.
/// This intentionally does not touch the hybrid data extractor.
/// </summary>
internal static class TooltipOverlayChinesePostProcessor
{
    public static string Process(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var s = text;

        // Clean any remaining marker artifacts from native tooltip capture.
        s = Regex.Replace(s, @"\b[HI](?:\s+[HI]){1,}\b", " ");
        s = Regex.Replace(s, @"\s{2,}", " ");
        s = Regex.Replace(s, @"\s+([。,.，:：])", "$1");

        // Common Starry Muse / shared recast notes.
        s = Rx(s, @"When\s+standing\s+within\s+the\s+bounds\s+of\s+Starry\s+Muse,?\s*consumes\s+a\s+stack\s+of\s+Hyperphantasia\s+if\s+available\.?", "站在 Starry Muse 范围内时，若有 Hyperphantasia 层数，则消耗 1 层。");
        s = Rx(s, @"This\s+action\s+does\s+not\s+share\s+a\s+recast\s+timer\s+with\s+any\s+other\s+actions\.?", "此技能不与其他技能共享复唱时间。");
        s = Rx(s, @"Upon\s+execution,?\s*the\s+recast\s+timer\s+for\s+this\s+action\s+will\s+be\s+applied\s+to\s+all\s+other\s+weaponskills\s+and\s+magic\s+actions\.?", "发动后，此技能的复唱时间会应用于所有其他战技与魔法技能。");

        // Mixed-language fragments caused by partial translation.
        s = Rx(s, @"the\s+复唱时间\s+for\s+this\s+action\s+will\s+be\s+applied\s+to\s+all\s+other\s+战技\s+and\s+魔法技能\.?", "此技能的复唱时间会应用于所有其他战技与魔法技能。");
        s = Rx(s, @"When\s+standing\s+within\s+the\s+bounds\s+of\s+Starry\s+Muse,?\s*消耗\s+a\s+stack\s+of\s+Hyperphantasia\s+if\s+available\.?", "站在 Starry Muse 范围内时，若有 Hyperphantasia 层数，则消耗 1 层。");

        // Rainbow Drip / instant cast style notes.
        s = Rx(s, @"When\s+Rainbow\s+Bright\s+is\s+active,?\s*Rainbow\s+Drip\s+can\s+be\s+cast\s+immediately,?\s*and\s+its\s+recast\s+timer\s+is\s+reduced\.?", "当 Rainbow Bright 激活时，Rainbow Drip 可立即发动，且复唱时间缩短。");

        // Execution conditions.
        s = Rx(s, @"Can\s+only\s+be\s+executed\s+while\s+under\s+the\s+effect\s+of\s+Subtractive\s+Palette\.?", "只能在 Subtractive Palette 效果期间发动。");
        s = Rx(s, @"Cannot\s+be\s+executed\s+while\s+under\s+the\s+effect\s+of\s+Subtractive\s+Palette\.?", "无法在 Subtractive Palette 效果期间发动。");

        // General labels/fragments that may slip through from action descriptions.
        s = Rx(s, @"\bAdditional\s+Effect\s*:\s*Grants\s+([A-Za-z][A-Za-z '\-]+?)\.?(?=\r?\n|$)", "追加效果：获得 $1。");
        s = Rx(s, @"\bGrants\s+Aetherhues\b", "获得 Aetherhues");
        s = Rx(s, @"\bGrants\s+White\s+Paint\b", "获得 White Paint");
        s = Rx(s, @"\bDuration\s*:\s*(\d+(?:\.\d+)?)s\b", "持续时间：$1秒");
        s = Rx(s, @"\bMaximum\s+Stacks\s*:\s*(\d+)\b", "最大层数：$1");

        // Light cleanup after replacements.
        s = Regex.Replace(s, @"\s{2,}", " ");
        s = Regex.Replace(s, @"\n\s+", "\n");
        return s.Trim();
    }

    private static string Rx(string input, string pattern, string replacement)
        => Regex.Replace(input, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);
}
