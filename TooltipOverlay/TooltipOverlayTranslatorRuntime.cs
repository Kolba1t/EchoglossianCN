// Copyright (c) fork author. Based on Echoglossian runtime patterns.
// Licensed under the same license terms as your Echoglossian fork.

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Echoglossian.TooltipOverlay;

internal sealed class TooltipOverlayTranslatorRuntime : IDisposable
{
    private readonly Config config;
    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly Func<string> targetLanguageCodeProvider;
    private readonly Func<object?> translationServiceProvider;
    private readonly GameTooltipTextProvider textProvider;
    private readonly Dictionary<string, TooltipPayload> cache = new(StringComparer.Ordinal);
    private readonly object gate = new();

    private TooltipLookupKey currentKey = TooltipLookupKey.None;
    private DateTime hoverStartedUtc = DateTime.MinValue;
    private Vector2 hoverAnchorMouse = Vector2.Zero;
    private string? suppressedCacheKey;
    private Vector2 suppressedAnchorMouse = Vector2.Zero;
    private CancellationTokenSource? currentTranslationCts;
    private Task? currentTranslationTask;
    private bool started;
    private string? pendingCacheKey;

    public TooltipOverlayTranslatorRuntime(
        Config config,
        IDataManager dataManager,
        IGameGui gameGui,
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        Func<string> targetLanguageCodeProvider,
        Func<object?> translationServiceProvider)
    {
        this.config = config;
        this.dataManager = dataManager;
        this.gameGui = gameGui;
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.targetLanguageCodeProvider = targetLanguageCodeProvider;
        this.translationServiceProvider = translationServiceProvider;
        this.textProvider = new GameTooltipTextProvider(dataManager, log);
    }

    public void Start()
    {
        if (this.started)
        {
            return;
        }

        this.started = true;
        this.gameGui.HoveredItemChanged += this.OnHoveredItemChanged;
        this.gameGui.HoveredActionChanged += this.OnHoveredActionChanged;
        this.pluginInterface.UiBuilder.Draw += this.Draw;
    }

    public void Dispose()
    {
        if (!this.started)
        {
            return;
        }

        this.started = false;
        this.gameGui.HoveredItemChanged -= this.OnHoveredItemChanged;
        this.gameGui.HoveredActionChanged -= this.OnHoveredActionChanged;
        this.pluginInterface.UiBuilder.Draw -= this.Draw;
        this.CancelPendingTranslation();
        lock (this.gate)
        {
            this.cache.Clear();
        }
    }

    private void OnHoveredItemChanged(object? sender, ulong hoveredItem)
    {
        this.UpdateCurrentHover(forceKey: GameTooltipTextProvider.FromHoveredItem(hoveredItem));
    }

    private void OnHoveredActionChanged(object? sender, HoveredAction hoveredAction)
    {
        // Item hovers have priority because actions and item details can briefly overlap
        // while the game's native tooltip UI is changing.
        if (this.gameGui.HoveredItem != 0)
        {
            return;
        }

        this.UpdateCurrentHover(forceKey: GameTooltipTextProvider.FromHoveredAction(hoveredAction));
    }

    private void Draw()
    {
        if (!this.ShouldRun())
        {
            return;
        }

        this.UpdateCurrentHover();
        if (this.currentKey.IsNone)
        {
            return;
        }

        if (this.ShouldClearBecauseCursorLeftHover())
        {
            this.ClearCurrentHover(suppressUntilMouseReturns: true);
            return;
        }

        var delayMs = this.ReadConfigInt("TooltipOverlayDelayMs", TooltipOverlayConfigDefaults.TooltipOverlayDelayMs, 0, 3000);
        if ((DateTime.UtcNow - this.hoverStartedUtc).TotalMilliseconds < delayMs)
        {
            return;
        }

        var payload = this.GetOrQueuePayload(this.currentKey);
        if (payload == null)
        {
            return;
        }

        this.DrawOverlay(payload);
    }

    private bool ShouldRun()
    {
        if (this.gameGui.GameUiHidden)
        {
            return false;
        }

        if (!this.config.Translate)
        {
            return false;
        }

        return this.ReadConfigBool("TranslateTooltipOverlay", TooltipOverlayConfigDefaults.TranslateTooltipOverlay);
    }

    private void UpdateCurrentHover(TooltipLookupKey? forceKey = null)
    {
        var nextKey = forceKey ?? this.ResolveCurrentHover();

        if (this.IsSuppressedByMouseDrift(nextKey))
        {
            nextKey = TooltipLookupKey.None;
        }
        else if (!nextKey.IsNone && this.suppressedCacheKey != null && nextKey.CacheKey != this.suppressedCacheKey)
        {
            this.suppressedCacheKey = null;
        }

        if (nextKey.Equals(this.currentKey))
        {
            return;
        }

        this.currentKey = nextKey;
        this.hoverStartedUtc = DateTime.UtcNow;
        this.hoverAnchorMouse = ImGui.GetMousePos();
        this.pendingCacheKey = null;
        this.CancelPendingTranslation();
    }

    private bool ShouldClearBecauseCursorLeftHover()
    {
        if (this.currentKey.IsNone)
        {
            return false;
        }

        var maxDrift = this.ReadConfigInt(
            "TooltipOverlayMaxMouseDriftPixels",
            TooltipOverlayConfigDefaults.TooltipOverlayMaxMouseDriftPixels,
            16,
            500);

        if (maxDrift <= 0)
        {
            return false;
        }

        var mouse = ImGui.GetMousePos();
        var delta = mouse - this.hoverAnchorMouse;
        if (delta.LengthSquared() <= maxDrift * maxDrift)
        {
            return false;
        }

        // If Dalamud reports a genuinely new hovered thing, UpdateCurrentHover will pick it up.
        // If it keeps returning the exact same key while the mouse has clearly left the icon/item,
        // treat that as stale and hide the overlay.
        var resolvedNow = this.ResolveCurrentHover();
        return resolvedNow.IsNone || resolvedNow.Equals(this.currentKey);
    }

    private bool IsSuppressedByMouseDrift(TooltipLookupKey nextKey)
    {
        if (nextKey.IsNone || this.suppressedCacheKey == null || nextKey.CacheKey != this.suppressedCacheKey)
        {
            return false;
        }

        var maxDrift = this.ReadConfigInt(
            "TooltipOverlayMaxMouseDriftPixels",
            TooltipOverlayConfigDefaults.TooltipOverlayMaxMouseDriftPixels,
            16,
            500);

        var mouse = ImGui.GetMousePos();
        var delta = mouse - this.suppressedAnchorMouse;
        if (delta.LengthSquared() <= maxDrift * maxDrift)
        {
            this.suppressedCacheKey = null;
            return false;
        }

        return true;
    }

    private void ClearCurrentHover(bool suppressUntilMouseReturns)
    {
        if (suppressUntilMouseReturns && !this.currentKey.IsNone)
        {
            this.suppressedCacheKey = this.currentKey.CacheKey;
            this.suppressedAnchorMouse = this.hoverAnchorMouse;
        }

        this.currentKey = TooltipLookupKey.None;
        this.hoverStartedUtc = DateTime.MinValue;
        this.pendingCacheKey = null;
        this.CancelPendingTranslation();
    }

    private TooltipLookupKey ResolveCurrentHover()
    {
        var itemKey = GameTooltipTextProvider.FromHoveredItem(this.gameGui.HoveredItem);
        if (!itemKey.IsNone)
        {
            return itemKey;
        }

        return GameTooltipTextProvider.FromHoveredAction(this.gameGui.HoveredAction);
    }

    private TooltipPayload? GetOrQueuePayload(TooltipLookupKey key)
    {
        lock (this.gate)
        {
            if (this.cache.TryGetValue(key.CacheKey, out var cached))
            {
                return cached;
            }
        }

        if (this.pendingCacheKey == key.CacheKey)
        {
            return new TooltipPayload(key, string.Empty, string.Empty, "正在翻译…", string.Empty, IsPending: true);
        }

        var source = this.textProvider.BuildSourcePayload(key);
        if (source == null)
        {
            return null;
        }

        this.pendingCacheKey = key.CacheKey;
        this.currentTranslationCts = new CancellationTokenSource();
        this.currentTranslationTask = Task.Run(
            () => this.TranslateAndCacheAsync(source, this.currentTranslationCts.Token),
            this.currentTranslationCts.Token);

        return new TooltipPayload(key, source.OriginalTitle, source.OriginalBody, "正在翻译…", string.Empty, IsPending: true);
    }

    private async Task TranslateAndCacheAsync(TooltipPayload source, CancellationToken cancellationToken)
    {
        try
        {
            var translator = this.translationServiceProvider();
            if (translator == null)
            {
                this.CachePayload(source with { Error = "Translation service is not ready." });
                return;
            }

            var targetLanguage = this.targetLanguageCodeProvider();
            if (string.IsNullOrWhiteSpace(targetLanguage))
            {
                this.CachePayload(source with { Error = "No target language selected." });
                return;
            }

            // v0.1.5 translates action names again. Earlier builds preserved action names
            // to avoid Imperator -> Emperor, but that made the overlay feel half-English.
            // If title translation fails or returns English, we fall back to the original.
            var translatedTitle = await this.TranslateTitleRobustAsync(
                translator,
                source.OriginalTitle,
                targetLanguage,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var bodyForTranslation = PrepareTextForTranslation(source.OriginalBody);
            var translatedBody = string.IsNullOrWhiteSpace(bodyForTranslation)
                ? string.Empty
                : await this.TranslateBodyRobustAsync(
                    translator,
                    bodyForTranslation,
                    targetLanguage,
                    cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            translatedBody = CleanTranslatedOutput(translatedBody);
            translatedBody = LocalizeKnownEnglishLabels(translatedBody);
            translatedBody = RestoreMissingNumericTokens(bodyForTranslation, translatedBody);

            // If the service still returns unchanged English for a non-English target, keep a
            // visible note rather than silently showing the user an untranslated overlay.
            if (!string.IsNullOrWhiteSpace(bodyForTranslation) && LooksUntranslated(bodyForTranslation, translatedBody, targetLanguage))
            {
                translatedBody = $"[未翻译 / untranslated]\n{bodyForTranslation}";
            }

            this.CachePayload(source with
            {
                TranslatedTitle = translatedTitle,
                TranslatedBody = translatedBody,
            });
        }
        catch (OperationCanceledException)
        {
            // Normal when the user moves the cursor away before the delay or translation completes.
        }
        catch (Exception ex)
        {
            this.log.Debug($"[CN Tooltip Overlay] Translation failed for {source.Key.CacheKey}: {ex}");
            this.CachePayload(source with
            {
                TranslatedTitle = source.OriginalTitle,
                TranslatedBody = source.OriginalBody,
                Error = ex.Message,
            });
        }
        finally
        {
            if (this.pendingCacheKey == source.Key.CacheKey)
            {
                this.pendingCacheKey = null;
            }
        }
    }


    private async Task<string> TranslateTitleRobustAsync(
        object translator,
        string originalTitle,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(originalTitle))
        {
            return string.Empty;
        }

        var cleanTitle = PrepareTextForTranslation(originalTitle);
        if (string.IsNullOrWhiteSpace(cleanTitle))
        {
            return originalTitle;
        }

        var translated = await this.TranslateWithBestAttemptAsync(
            translator,
            cleanTitle,
            targetLanguage,
            "TooltipOverlay.Title.TranslateAbilityOrItemNameToChinese.KeepNumbers",
            cancellationToken).ConfigureAwait(false);

        translated = CleanTranslatedOutput(translated);
        if (LooksUntranslated(cleanTitle, translated, targetLanguage))
        {
            return originalTitle;
        }

        return translated;
    }


    private async Task<string> TranslateBodyRobustAsync(
        object translator,
        string bodyForTranslation,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        // v0.1.5b: Always translate tooltip bodies line-by-line. Earlier builds translated
        // the full block first; generated stat lines such as Cast time/Range could be
        // localized locally, causing the whole block to look "translated" even while the
        // actual action description stayed English. Per-line handling is slower on a cold
        // cache, but cached hovers are instant and it is much more reliable for tooltips.
        return await this.TranslateBodyLineByLineAsync(
            translator,
            bodyForTranslation,
            targetLanguage,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> TranslateBodyLineByLineAsync(
        object translator,
        string bodyForTranslation,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var output = new List<string>();
        foreach (var rawLine in bodyForTranslation.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                output.Add(string.Empty);
                continue;
            }

            var local = LocalizeSupplementLine(line);
            if (local != null)
            {
                output.Add(local);
                continue;
            }

            var translatedLine = await this.TranslateWithBestAttemptAsync(
                translator,
                line,
                targetLanguage,
                "TooltipOverlay.BodyLine.PreserveNumbersAndFFXIVTerms",
                cancellationToken).ConfigureAwait(false);

            translatedLine = CleanTranslatedOutput(translatedLine);
            translatedLine = LocalizeKnownEnglishLabels(translatedLine);

            // If the backend still refuses to translate this specific line, keep the original
            // visible with a marker instead of silently pretending it translated.
            output.Add(LooksUntranslated(line, translatedLine, targetLanguage)
                ? $"[未翻译] {line}"
                : translatedLine);
        }

        return string.Join("\n", output).Trim();
    }

    private static string? LocalizeSupplementLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var trimmed = line.Trim();
        if (trimmed.Equals("Action data:", StringComparison.OrdinalIgnoreCase))
        {
            return "技能数据：";
        }

        if (trimmed.Equals("Additional data:", StringComparison.OrdinalIgnoreCase))
        {
            return "附加数据：";
        }

        var match = Regex.Match(trimmed, @"^(?<label>Skill type|Potency|Potency values|Level|Cast time|Recast time|Range|Radius|Width/axis modifier|Maximum charges|Primary cost|Secondary cost|Cost|CP|GP):\s*(?<value>.+)$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var label = match.Groups["label"].Value.ToLowerInvariant();
        var value = match.Groups["value"].Value.Trim();
        value = Regex.Replace(value, @"\byalms?\b", "米", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bInstant\b", "即时", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bWeaponskill\b", "战技", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bSpell\b", "魔法", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bAbility\b", "能力", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"(?<=\d)s\b", "秒", RegexOptions.IgnoreCase);

        var zhLabel = label switch
        {
            "skill type" => "技能类型",
            "potency" => "威力",
            "potency values" => "威力数值",
            "level" => "等级",
            "cast time" => "咏唱时间",
            "recast time" => "复唱时间",
            "range" => "距离",
            "radius" => "范围半径",
            "width/axis modifier" => "宽度/轴向修正",
            "maximum charges" => "最大积蓄次数",
            "primary cost" => "主要消耗",
            "secondary cost" => "次要消耗",
            "cost" => "消耗",
            "cp" => "制作力",
            "gp" => "采集力",
            _ => null,
        };

        return zhLabel == null ? null : $"{zhLabel}：{value}";
    }

    private static string LocalizeKnownEnglishLabels(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = LocalizeSupplementLine(lines[i]) ?? lines[i];
        }

        return string.Join("\n", lines);
    }


    private static string PrepareTextForTranslation(string text)
    {
        var clean = ExcelReflection.CleanGameText(text);
        if (string.IsNullOrWhiteSpace(clean))
        {
            return string.Empty;
        }

        // Do not prefix instructions into the text itself: non-LLM backends such as
        // Google can translate the instruction literally. Numeric preservation is handled
        // by the context string plus RestoreMissingNumericTokens().
        return clean;
    }

    private static string CleanTranslatedOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var clean = text.Replace("\r\n", "\n").Replace('\r', '\n');
        clean = Regex.Replace(clean, @"^\s*[-—]{3,}\s*", string.Empty, RegexOptions.Multiline);
        clean = Regex.Replace(clean, @"\n{3,}", "\n\n");
        return clean.Trim();
    }

    private static string RestoreMissingNumericTokens(string source, string translated)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translated))
        {
            return translated;
        }

        var sourceCore = StripTranslationInstruction(source);
        var sourceTokens = ExtractNumericTokens(sourceCore).Distinct(StringComparer.Ordinal).ToList();
        if (sourceTokens.Count == 0)
        {
            return translated;
        }

        var translatedTokens = ExtractNumericTokens(translated).ToHashSet(StringComparer.Ordinal);
        var missing = sourceTokens.Where(t => !translatedTokens.Contains(t)).Take(16).ToList();
        if (missing.Count == 0)
        {
            return translated;
        }

        return translated.TrimEnd() + "\n\n数值保留: " + string.Join(", ", missing);
    }

    private static IEnumerable<string> ExtractNumericTokens(string text)
    {
        // Handles potency/cooldown/range-like values: 300, 2.50s, 10%, 1,000, 15-yalm.
        foreach (Match match in Regex.Matches(text, @"(?<![A-Za-z])\d{1,3}(?:,\d{3})*(?:\.\d+)?(?:\s?[%s秒秒钟]|\s?yalms?|\s?yalm)?|(?<![A-Za-z])\d+(?:\.\d+)?(?:\s?[%s秒秒钟]|\s?yalms?|\s?yalm)?", RegexOptions.IgnoreCase))
        {
            var value = Regex.Replace(match.Value.Trim(), @"\s+", string.Empty);
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    private static bool LooksUntranslated(string source, string translated, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(translated))
        {
            return false;
        }

        if (!IsChineseTarget(targetLanguage))
        {
            return false;
        }

        // For a Chinese target, a successful tooltip translation should contain at least
        // some CJK characters. v0.1.3 only treated text as untranslated when it looked
        // nearly identical to the source, which let English paraphrases get cached as if
        // they were successful translations.
        if (ContainsCjk(translated))
        {
            return false;
        }

        var latinLetters = translated.Count(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));
        if (latinLetters >= 8)
        {
            return true;
        }

        var sourceCore = NormalizeForComparison(StripTranslationInstruction(source));
        var translatedCore = NormalizeForComparison(translated);
        if (string.IsNullOrWhiteSpace(sourceCore) || string.IsNullOrWhiteSpace(translatedCore))
        {
            return false;
        }

        return translatedCore.Contains(sourceCore, StringComparison.OrdinalIgnoreCase) ||
               sourceCore.Contains(translatedCore, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripTranslationInstruction(string source)
    {
        var marker = "---\n";
        var index = source.IndexOf(marker, StringComparison.Ordinal);
        return index >= 0 ? source[(index + marker.Length)..] : source;
    }

    private static string NormalizeForComparison(string value)
    {
        return Regex.Replace(value, @"[^A-Za-z0-9]+", " ").Trim();
    }

    private static bool IsChineseTarget(string targetLanguage)
    {
        return targetLanguage.Contains("zh", StringComparison.OrdinalIgnoreCase) ||
               targetLanguage.Contains("chinese", StringComparison.OrdinalIgnoreCase) ||
               targetLanguage.Contains("中文", StringComparison.OrdinalIgnoreCase) ||
               targetLanguage.Contains("Chinese", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsCjk(string value)
    {
        return value.Any(c => c >= '\u3400' && c <= '\u9FFF');
    }


    private async Task<string> TranslateWithBestAttemptAsync(
        object translator,
        string text,
        string targetLanguage,
        string context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var attempts = new List<(string Text, string Source, string Target, string Context)>
        {
            (text, "English", targetLanguage, context),
            (text, "en", targetLanguage, context),
        };

        var humanTarget = HumanizeTargetLanguage(targetLanguage);
        if (!string.Equals(humanTarget, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            attempts.Add((text, "English", humanTarget, context));
            attempts.Add((text, "en", humanTarget, context));
        }

        var codeTarget = CodeTargetLanguage(targetLanguage);
        if (!string.Equals(codeTarget, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            attempts.Add((text, "English", codeTarget, context));
            attempts.Add((text, "en", codeTarget, context));
        }

        // Last resort for backends that ignore the source/target parameters from this
        // reflection call path. This is especially useful for OpenAI-compatible providers.
        // We only use instruction wrapping if the normal calls do not produce CJK text.
        var instructionWrapped = BuildInstructionWrappedText(text, targetLanguage, context);
        attempts.Add((instructionWrapped, "English", targetLanguage, context + ".InstructionWrapped"));
        attempts.Add((instructionWrapped, "en", humanTarget, context + ".InstructionWrapped"));

        var best = string.Empty;
        foreach (var attempt in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string candidate;
            try
            {
                candidate = await this.TranslateWithServiceAsync(
                    translator,
                    attempt.Text,
                    attempt.Source,
                    attempt.Target,
                    attempt.Context,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.log.Debug($"[CN Tooltip Overlay] Translation attempt failed ({attempt.Source}->{attempt.Target}, {attempt.Context}): {ex.Message}");
                continue;
            }

            candidate = ExtractInstructionWrappedResult(candidate);
            candidate = CleanTranslatedOutput(candidate);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (IsBetterTranslationCandidate(text, candidate, best, targetLanguage))
            {
                best = candidate;
            }

            if (!LooksUntranslated(text, candidate, targetLanguage))
            {
                return candidate;
            }
        }

        return best;
    }

    private static bool IsBetterTranslationCandidate(string source, string candidate, string currentBest, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(currentBest))
        {
            return true;
        }

        if (IsChineseTarget(targetLanguage))
        {
            var candidateCjk = candidate.Count(c => c >= '\u3400' && c <= '\u9FFF');
            var bestCjk = currentBest.Count(c => c >= '\u3400' && c <= '\u9FFF');
            if (candidateCjk != bestCjk)
            {
                return candidateCjk > bestCjk;
            }

            var candidateLatin = candidate.Count(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));
            var bestLatin = currentBest.Count(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));
            if (candidateLatin != bestLatin)
            {
                return candidateLatin < bestLatin;
            }
        }

        // Prefer candidates that keep roughly the same numeric information.
        var sourceNums = ExtractNumericTokens(source).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var candidateNums = ExtractNumericTokens(candidate).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var bestNums = ExtractNumericTokens(currentBest).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var candidateKept = sourceNums.Count(n => candidateNums.Contains(n));
        var bestKept = sourceNums.Count(n => bestNums.Contains(n));
        if (candidateKept != bestKept)
        {
            return candidateKept > bestKept;
        }

        return candidate.Length > currentBest.Length;
    }

    private static string HumanizeTargetLanguage(string targetLanguage)
    {
        if (IsChineseTarget(targetLanguage))
        {
            if (targetLanguage.Contains("tw", StringComparison.OrdinalIgnoreCase) ||
                targetLanguage.Contains("traditional", StringComparison.OrdinalIgnoreCase) ||
                targetLanguage.Contains("繁", StringComparison.OrdinalIgnoreCase))
            {
                return "Traditional Chinese";
            }

            return "Simplified Chinese";
        }

        return targetLanguage;
    }

    private static string CodeTargetLanguage(string targetLanguage)
    {
        if (IsChineseTarget(targetLanguage))
        {
            if (targetLanguage.Contains("tw", StringComparison.OrdinalIgnoreCase) ||
                targetLanguage.Contains("traditional", StringComparison.OrdinalIgnoreCase) ||
                targetLanguage.Contains("繁", StringComparison.OrdinalIgnoreCase))
            {
                return "zh-TW";
            }

            return "zh-CN";
        }

        return targetLanguage;
    }

    private static string BuildInstructionWrappedText(string text, string targetLanguage, string context)
    {
        var humanTarget = HumanizeTargetLanguage(targetLanguage);
        return $"Translate the following FINAL FANTASY XIV tooltip text into {humanTarget}. " +
               "Keep all numbers, percentages, cooldowns, potency values, ranges, line breaks, and symbols. " +
               "Do not explain. Return only the translation between <translation> and </translation>. " +
               "Translate action names and descriptions.\n" +
               $"Context: {context}\n" +
               "<translation>\n" +
               text.Trim() +
               "\n</translation>";
    }

    private static string ExtractInstructionWrappedResult(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }

        var match = Regex.Match(candidate, @"<translation>\s*(?<body>.*?)\s*</translation>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["body"].Value.Trim() : candidate;
    }

    private async Task<string> TranslateWithServiceAsync(
        object translator,
        string text,
        string sourceLanguage,
        string targetLanguage,
        string context,
        CancellationToken cancellationToken)
    {
        var methods = translator.GetType()
            .GetMethods()
            .Where(m => m.Name == "TranslateAsync")
            .ToArray();

        // Echoglossian's runtime handlers call TranslationService.TranslateAsync(text,
        // ClientLanguage.Humanize(), LangDict[LanguageInt].Code). Prefer that exact
        // three-argument shape. v0.1.3 selected the overload with the most parameters,
        // which can route through an overload not meant for normal translations and cache
        // English output as if it were translated.
        var method = methods.FirstOrDefault(m =>
        {
            var p = m.GetParameters();
            return p.Length == 3 && p.All(x => x.ParameterType == typeof(string));
        }) ?? methods.FirstOrDefault(m =>
        {
            var p = m.GetParameters();
            return p.Length == 3 && p.Count(x => x.ParameterType == typeof(string)) >= 3;
        }) ?? methods
            .OrderBy(m => m.GetParameters().Length)
            .FirstOrDefault(m => m.GetParameters().Length >= 3);

        if (method == null)
        {
            throw new MissingMethodException(translator.GetType().FullName, "TranslateAsync");
        }

        var parameters = method.GetParameters();
        object?[] args;
        if (parameters.Length == 3 && parameters.Count(x => x.ParameterType == typeof(string)) >= 3)
        {
            args = new object?[] { text, sourceLanguage, targetLanguage };
        }
        else
        {
            args = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                var name = parameter.Name?.ToLowerInvariant() ?? string.Empty;

                if (i == 0 && parameter.ParameterType == typeof(string))
                {
                    args[i] = text;
                }
                else if (parameter.ParameterType == typeof(CancellationToken))
                {
                    args[i] = cancellationToken;
                }
                else if (parameter.ParameterType == typeof(string) && (name.Contains("source") || name.Contains("from") || name.Contains("origin")))
                {
                    args[i] = sourceLanguage;
                }
                else if (parameter.ParameterType == typeof(string) && (name.Contains("target") || name.Contains("to") || name.Contains("dest")))
                {
                    args[i] = targetLanguage;
                }
                else if (parameter.ParameterType == typeof(string) && name.Contains("context"))
                {
                    args[i] = context;
                }
                else if (parameter.ParameterType == typeof(string) && i == 1)
                {
                    args[i] = sourceLanguage;
                }
                else if (parameter.ParameterType == typeof(string) && i == 2)
                {
                    args[i] = targetLanguage;
                }
                else if (parameter.HasDefaultValue)
                {
                    args[i] = parameter.DefaultValue;
                }
                else if (parameter.ParameterType == typeof(string))
                {
                    args[i] = context;
                }
                else
                {
                    args[i] = parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;
                }
            }
        }

        var result = method.Invoke(translator, args);
        return await UnwrapTranslationResultAsync(result).ConfigureAwait(false);
    }

    private static async Task<string> UnwrapTranslationResultAsync(object? result)
    {
        if (result == null)
        {
            return string.Empty;
        }

        if (result is string direct)
        {
            return direct;
        }

        if (result is Task<string> typedTask)
        {
            return await typedTask.ConfigureAwait(false);
        }

        if (result is Task task)
        {
            await task.ConfigureAwait(false);
            var resultProperty = task.GetType().GetProperty("Result");
            return resultProperty?.GetValue(task)?.ToString() ?? string.Empty;
        }

        var asTaskMethod = result.GetType().GetMethod("AsTask", Type.EmptyTypes);
        if (asTaskMethod?.Invoke(result, Array.Empty<object>()) is Task asTask)
        {
            await asTask.ConfigureAwait(false);
            var resultProperty = asTask.GetType().GetProperty("Result");
            return resultProperty?.GetValue(asTask)?.ToString() ?? string.Empty;
        }

        return result.ToString() ?? string.Empty;
    }

    private void CachePayload(TooltipPayload payload)
    {
        lock (this.gate)
        {
            this.cache[payload.Key.CacheKey] = payload;

            // Keep the cache bounded; tooltip text repeats a lot, but long sessions can hover
            // thousands of items. This prevents unbounded memory growth.
            if (this.cache.Count > 500)
            {
                foreach (var key in this.cache.Keys.Take(100).ToArray())
                {
                    this.cache.Remove(key);
                }
            }
        }
    }

    private void DrawOverlay(TooltipPayload payload)
    {
        var maxWidth = this.ReadConfigInt("TooltipOverlayMaxWidth", TooltipOverlayConfigDefaults.TooltipOverlayMaxWidth, 260, 1000);
        var fontScale = this.ReadConfigFloat("TooltipOverlayFontScale", TooltipOverlayConfigDefaults.TooltipOverlayFontScale, 0.6f, 2.5f);
        var bgAlpha = this.ReadConfigFloat("TooltipOverlayBgAlpha", TooltipOverlayConfigDefaults.TooltipOverlayBgAlpha, 0.2f, 1f);
        var includeOriginal = this.ReadConfigBool("TooltipOverlayShowOriginal", TooltipOverlayConfigDefaults.TooltipOverlayShowOriginal);

        var mouse = ImGui.GetMousePos();
        var viewport = ImGui.GetMainViewport();
        var pos = this.ClampOverlayInitialPosition(mouse + new Vector2(28, 24), mouse, viewport.WorkPos, viewport.WorkSize, maxWidth);

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(bgAlpha);
        ImGui.SetNextWindowSizeConstraints(new Vector2(260, 0), new Vector2(maxWidth, float.MaxValue));

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoMove;

        if (!ImGui.Begin("###EchoglossianCNTooltipOverlay", flags))
        {
            ImGui.End();
            return;
        }

        const float oldScale = 1.0f;
        ImGui.SetWindowFontScale(fontScale);

        if (!string.IsNullOrWhiteSpace(payload.Key.DisplayKind))
        {
            ImGui.TextDisabled($"{payload.Key.DisplayKind} #{payload.Key.RowId}");
        }

        this.DrawWrapped(payload.TranslatedTitle, fallback: payload.OriginalTitle, strong: true);
        this.DrawWrapped(payload.TranslatedBody, fallback: payload.OriginalBody, strong: false);

        if (!string.IsNullOrWhiteSpace(payload.Error))
        {
            ImGui.Separator();
            ImGui.TextDisabled($"翻译失败：{payload.Error}");
        }

        if (includeOriginal && (!string.IsNullOrWhiteSpace(payload.OriginalTitle) || !string.IsNullOrWhiteSpace(payload.OriginalBody)))
        {
            ImGui.Separator();
            ImGui.TextDisabled("Original");
            this.DrawWrapped(payload.OriginalTitle, fallback: string.Empty, strong: true);
            this.DrawWrapped(payload.OriginalBody, fallback: string.Empty, strong: false);
        }

        this.ClampCurrentOverlayWindowToViewport(viewport.WorkPos, viewport.WorkSize);

        ImGui.SetWindowFontScale(oldScale);
        ImGui.End();
    }


    private Vector2 ClampOverlayInitialPosition(Vector2 desired, Vector2 mouse, Vector2 workPosRaw, Vector2 workSizeRaw, float maxWidth)
    {
        var margin = 16f;
        var workMin = workPosRaw + new Vector2(margin, margin);
        var workMax = workPosRaw + workSizeRaw - new Vector2(margin, margin);

        var pos = desired;
        if (pos.X + maxWidth > workMax.X)
        {
            pos.X = Math.Max(workMin.X, mouse.X - maxWidth - 24f);
        }

        // Initial vertical clamp uses a conservative estimated height. The final exact
        // clamp runs after ImGui has measured the auto-resized window.
        var estimatedHeight = Math.Min(520f, Math.Max(160f, workSizeRaw.Y * 0.45f));
        if (pos.Y + estimatedHeight > workMax.Y)
        {
            pos.Y = Math.Max(workMin.Y, mouse.Y - estimatedHeight - 24f);
        }

        pos.X = Math.Clamp(pos.X, workMin.X, Math.Max(workMin.X, workMax.X - 120f));
        pos.Y = Math.Clamp(pos.Y, workMin.Y, Math.Max(workMin.Y, workMax.Y - 80f));
        return pos;
    }

    private void ClampCurrentOverlayWindowToViewport(Vector2 workPosRaw, Vector2 workSizeRaw)
    {
        var margin = 16f;
        var workMin = workPosRaw + new Vector2(margin, margin);
        var workMax = workPosRaw + workSizeRaw - new Vector2(margin, margin);
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();

        var next = pos;
        if (next.X + size.X > workMax.X)
        {
            next.X = workMax.X - size.X;
        }

        if (next.Y + size.Y > workMax.Y)
        {
            next.Y = workMax.Y - size.Y;
        }

        if (next.X < workMin.X)
        {
            next.X = workMin.X;
        }

        if (next.Y < workMin.Y)
        {
            next.Y = workMin.Y;
        }

        if ((next - pos).LengthSquared() > 0.25f)
        {
            ImGui.SetWindowPos(next, ImGuiCond.Always);
        }
    }

    private void DrawWrapped(string text, string fallback, bool strong)
    {
        var value = string.IsNullOrWhiteSpace(text) ? fallback : text;
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (strong)
        {
            ImGui.TextWrapped(value);
            ImGui.Separator();
        }
        else
        {
            ImGui.TextWrapped(value);
        }
    }

    private void CancelPendingTranslation()
    {
        try
        {
            this.currentTranslationCts?.Cancel();
            this.currentTranslationCts?.Dispose();
        }
        catch
        {
            // ignored
        }

        this.currentTranslationCts = null;
        this.currentTranslationTask = null;
    }

    private bool ReadConfigBool(string fieldName, bool fallback)
    {
        var field = typeof(Config).GetField(fieldName);
        return field?.GetValue(this.config) is bool value ? value : fallback;
    }

    private int ReadConfigInt(string fieldName, int fallback, int min, int max)
    {
        var field = typeof(Config).GetField(fieldName);
        var raw = field?.GetValue(this.config);
        if (raw == null)
        {
            return fallback;
        }

        try
        {
            return Math.Clamp(Convert.ToInt32(raw), min, max);
        }
        catch
        {
            return fallback;
        }
    }

    private float ReadConfigFloat(string fieldName, float fallback, float min, float max)
    {
        var field = typeof(Config).GetField(fieldName);
        var raw = field?.GetValue(this.config);
        if (raw == null)
        {
            return fallback;
        }

        try
        {
            return Math.Clamp(Convert.ToSingle(raw), min, max);
        }
        catch
        {
            return fallback;
        }
    }
}
