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
    private Vector2 lastOverlaySize = new(420, 260);

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
        this.textProvider = new GameTooltipTextProvider(dataManager, gameGui, log);
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

            // v14: translate action titles again. Earlier builds preserved action titles to avoid
            // errors like "Imperator" -> "Emperor", but users need Mandarin title support.
            // If this causes specific bad proper-noun translations later, handle that with a
            // small glossary instead of skipping all action titles.
            var translatedTitle = string.IsNullOrWhiteSpace(source.OriginalTitle)
                ? string.Empty
                : await this.TranslateWithServiceAsync(
                    translator,
                    source.OriginalTitle,
                    "English",
                    targetLanguage,
                    "TooltipOverlay.Title.KeepFFXIVSkillNameMeaning",
                    cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var bodyForTranslation = PrepareTextForTranslation(source.OriginalBody);
            var translatedBody = string.IsNullOrWhiteSpace(bodyForTranslation)
                ? string.Empty
                : await this.TranslateStructuredTooltipBodyAsync(
                    translator,
                    bodyForTranslation,
                    targetLanguage,
                    cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            translatedBody = CleanTranslatedOutput(translatedBody);
            translatedBody = LocalizeKnownEnglishLabels(translatedBody);
            translatedBody = RestoreMissingNumericTokens(bodyForTranslation, translatedBody);

            // If the service still returns unchanged English for a non-English target, do not
            // throw away partially-localized labels/stat lines. Earlier builds replaced the
            // entire body with the original English block, which made fixes look worse than
            // they were. Keep partial output visible and mark it as partial.
            if (!string.IsNullOrWhiteSpace(bodyForTranslation) && LooksUntranslated(bodyForTranslation, translatedBody, targetLanguage))
            {
                var partial = LocalizeKnownEnglishLabels(CleanTranslatedOutput(translatedBody));
                translatedBody = ContainsCjk(partial)
                    ? $"[部分未翻译 / partial]\n{partial}"
                    : $"[未翻译 / untranslated]\n{bodyForTranslation}";
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


    private async Task<string> TranslateStructuredTooltipBodyAsync(
        object translator,
        string bodyForTranslation,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var sections = TooltipBodySections.Parse(bodyForTranslation);
        if (!sections.HasDescription)
        {
            return await this.TranslateBodyRobustAsync(
                translator,
                bodyForTranslation,
                targetLanguage,
                cancellationToken).ConfigureAwait(false);
        }

        var output = new List<string>();
        foreach (var line in sections.HeaderLines)
        {
            var localized = LocalizeSupplementLine(line) ?? line.Trim();
            if (!string.IsNullOrWhiteSpace(localized))
            {
                output.Add(localized);
            }
        }

        if (sections.DescriptionLines.Count > 0)
        {
            var descriptionSource = NormalizeDescriptionBlockForTranslation(string.Join("\n", sections.DescriptionLines));
            var parsedDescription = StructuredDescriptionSections.Parse(descriptionSource);

            if (parsedDescription.HasAnyStructuredContent)
            {
                await this.AppendStructuredDescriptionSectionsAsync(
                    output,
                    parsedDescription,
                    translator,
                    targetLanguage,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (output.Count > 0)
                {
                    output.Add(string.Empty);
                }

                output.Add("说明：");
                var translatedDescription = await this.TranslateDescriptionBlockAsync(
                    translator,
                    descriptionSource,
                    targetLanguage,
                    cancellationToken).ConfigureAwait(false);

                translatedDescription = CleanTranslatedOutput(translatedDescription);
                if (string.IsNullOrWhiteSpace(translatedDescription) || LooksUntranslated(descriptionSource, translatedDescription, targetLanguage))
                {
                    var localFallback = LocalizeDescriptionFallback(descriptionSource);
                    if (!string.IsNullOrWhiteSpace(localFallback) && ContainsCjk(localFallback))
                    {
                        output.Add(localFallback);
                    }
                    else
                    {
                        output.Add("[说明未翻译 / description untranslated]");
                        output.Add(descriptionSource);
                    }
                }
                else
                {
                    output.Add(translatedDescription);
                }
            }
        }

        if (sections.ActionDataLines.Count > 0)
        {
            if (output.Count > 0)
            {
                output.Add(string.Empty);
            }

            output.Add("技能数据：");
            foreach (var line in sections.ActionDataLines)
            {
                var localized = LocalizeSupplementLine(line) ?? line.Trim();
                if (!string.IsNullOrWhiteSpace(localized) && !localized.Equals("技能数据：", StringComparison.Ordinal))
                {
                    output.Add(localized);
                }
            }
        }

        foreach (var line in sections.TrailingLines)
        {
            var localized = LocalizeSupplementLine(line) ?? line.Trim();
            if (!string.IsNullOrWhiteSpace(localized))
            {
                output.Add(localized);
            }
        }

        return CleanTranslatedOutput(string.Join("\n", output));
    }

    private async Task AppendStructuredDescriptionSectionsAsync(
        List<string> output,
        StructuredDescriptionSections sections,
        object translator,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        await this.AppendDescriptionSectionAsync(output, "说明：", sections.MainLines, translator, targetLanguage, cancellationToken, TooltipDescriptionSectionKind.Main).ConfigureAwait(false);
        await this.AppendDescriptionSectionAsync(output, "追加效果：", sections.AdditionalEffectLines, translator, targetLanguage, cancellationToken, TooltipDescriptionSectionKind.AdditionalEffect).ConfigureAwait(false);
        await this.AppendDescriptionSectionAsync(output, "持续时间：", sections.DurationLines, translator, targetLanguage, cancellationToken, TooltipDescriptionSectionKind.Duration).ConfigureAwait(false);
        await this.AppendDescriptionSectionAsync(output, "连击加成：", sections.ComboBonusLines, translator, targetLanguage, cancellationToken, TooltipDescriptionSectionKind.ComboBonus).ConfigureAwait(false);
        await this.AppendDescriptionSectionAsync(output, "发动条件：", sections.RequirementLines, translator, targetLanguage, cancellationToken, TooltipDescriptionSectionKind.Requirement).ConfigureAwait(false);
        await this.AppendDescriptionSectionAsync(output, "特殊说明：", sections.SpecialNoteLines, translator, targetLanguage, cancellationToken, TooltipDescriptionSectionKind.SpecialNote).ConfigureAwait(false);
        await this.AppendDescriptionSectionAsync(output, "其他说明：", sections.OtherLines, translator, targetLanguage, cancellationToken, TooltipDescriptionSectionKind.Other).ConfigureAwait(false);
    }

    private async Task AppendDescriptionSectionAsync(
        List<string> output,
        string zhHeader,
        IReadOnlyList<string> sourceLines,
        object translator,
        string targetLanguage,
        CancellationToken cancellationToken,
        TooltipDescriptionSectionKind kind)
    {
        if (sourceLines.Count == 0)
        {
            return;
        }

        var renderedLines = new List<string>();
        foreach (var line in sourceLines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rendered = await this.TranslateOrLocalizeDescriptionLineAsync(
                line,
                translator,
                targetLanguage,
                cancellationToken,
                kind).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(rendered))
            {
                renderedLines.Add(rendered);
            }
        }

        if (renderedLines.Count == 0)
        {
            return;
        }

        if (output.Count > 0)
        {
            output.Add(string.Empty);
        }

        output.Add(zhHeader);
        output.AddRange(renderedLines);
    }

    private async Task<string> TranslateOrLocalizeDescriptionLineAsync(
        string sourceLine,
        object translator,
        string targetLanguage,
        CancellationToken cancellationToken,
        TooltipDescriptionSectionKind kind)
    {
        var cleanLine = NormalizeDescriptionBlockForTranslation(sourceLine);
        cleanLine = RemoveLeadingEnglishDescriptionLabel(cleanLine, kind);
        cleanLine = CleanupDescriptionControlMarkers(cleanLine);
        if (string.IsNullOrWhiteSpace(cleanLine))
        {
            return string.Empty;
        }

        var local = LocalizeDescriptionLineFallback(cleanLine);
        local = StripDuplicatedSectionPrefix(local, kind);
        if (!string.IsNullOrWhiteSpace(local) && ContainsCjk(local) && !LooksUntranslated(cleanLine, local, targetLanguage))
        {
            return CleanTranslatedOutput(local);
        }

        var translated = await this.TranslateDescriptionBlockAsync(
            translator,
            cleanLine,
            targetLanguage,
            cancellationToken).ConfigureAwait(false);

        translated = CleanTranslatedOutput(translated);
        translated = StripDuplicatedSectionPrefix(translated, kind);
        translated = RestoreMissingNumericTokens(cleanLine, translated);

        if (!string.IsNullOrWhiteSpace(translated) && !LooksUntranslated(cleanLine, translated, targetLanguage))
        {
            return translated;
        }

        if (!string.IsNullOrWhiteSpace(local) && ContainsCjk(local))
        {
            return CleanTranslatedOutput(local);
        }

        return cleanLine;
    }

    private enum TooltipDescriptionSectionKind
    {
        Main,
        AdditionalEffect,
        Duration,
        ComboBonus,
        Requirement,
        SpecialNote,
        Other,
    }

    private static string RemoveLeadingEnglishDescriptionLabel(string line, TooltipDescriptionSectionKind kind)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var clean = line.Trim();
        clean = Regex.Replace(clean, @"^Description\s*:\s*", string.Empty, RegexOptions.IgnoreCase);

        clean = kind switch
        {
            TooltipDescriptionSectionKind.AdditionalEffect => Regex.Replace(clean, @"^Additional\s+Effect\s*:\s*", string.Empty, RegexOptions.IgnoreCase),
            TooltipDescriptionSectionKind.Duration => Regex.Replace(clean, @"^Duration\s*:\s*", string.Empty, RegexOptions.IgnoreCase),
            TooltipDescriptionSectionKind.ComboBonus => Regex.Replace(clean, @"^Combo\s+Bonus\s*:\s*", string.Empty, RegexOptions.IgnoreCase),
            _ => clean,
        };

        return clean.Trim();
    }

    private static string StripDuplicatedSectionPrefix(string text, TooltipDescriptionSectionKind kind)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var clean = text.Trim();
        clean = kind switch
        {
            TooltipDescriptionSectionKind.AdditionalEffect => Regex.Replace(clean, @"^追加效果[：:]\s*", string.Empty),
            TooltipDescriptionSectionKind.Duration => Regex.Replace(clean, @"^持续时间[：:]\s*", string.Empty),
            TooltipDescriptionSectionKind.ComboBonus => Regex.Replace(clean, @"^连击加成[：:]\s*", string.Empty),
            TooltipDescriptionSectionKind.Requirement => Regex.Replace(clean, @"^发动条件[：:]\s*", string.Empty),
            TooltipDescriptionSectionKind.SpecialNote => Regex.Replace(clean, @"^特殊说明[：:]\s*", string.Empty),
            TooltipDescriptionSectionKind.Other => Regex.Replace(clean, @"^其他说明[：:]\s*", string.Empty),
            _ => Regex.Replace(clean, @"^说明[：:]\s*", string.Empty),
        };

        return clean.Trim();
    }

    private static string LocalizeDescriptionFallback(string descriptionSource)
    {
        if (string.IsNullOrWhiteSpace(descriptionSource))
        {
            return string.Empty;
        }

        var text = NormalizeDescriptionBlockForTranslation(descriptionSource);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var output = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                if (output.Count > 0 && !string.IsNullOrWhiteSpace(output[^1]))
                {
                    output.Add(string.Empty);
                }
                continue;
            }

            line = CleanupDescriptionControlMarkers(line);
            var localized = LocalizeDescriptionLineFallback(line);
            if (!string.IsNullOrWhiteSpace(localized))
            {
                output.Add(localized);
            }
        }

        return CleanTranslatedOutput(string.Join("\n", output));
    }

    private static string CleanupDescriptionControlMarkers(string line)
    {
        var clean = Regex.Replace(line, @"\s+", " ").Trim();
        clean = Regex.Replace(clean, @"\b[HI]\b", " ");
        clean = Regex.Replace(clean, @"\s+([,.;:])", "$1");
        clean = Regex.Replace(clean, @"([:])(?=\S)", "$1 ");
        clean = Regex.Replace(clean, @"\s{2,}", " ").Trim();
        return clean;
    }

    private static string LocalizeDescriptionLineFallback(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var original = CleanupDescriptionControlMarkers(line);
        var working = original;
        working = Regex.Replace(working, @"^Description\s*:\s*", string.Empty, RegexOptions.IgnoreCase).Trim();

        // Very common FFXIV action-tooltip sentence patterns. This is intentionally a
        // conservative fallback: it only rewrites shapes we understand and keeps unknown
        // status/proper-noun names visible instead of guessing.
        var m = Regex.Match(working, @"^Deals\s+(?<element>[A-Za-z]+)\s+damage\s+with\s+a\s+potency\s+of\s+(?<potency>\d+(?:\.\d+)?)\.?$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return $"造成{LocalizeElement(m.Groups["element"].Value)}属性伤害，威力为{m.Groups["potency"].Value}。";
        }

        m = Regex.Match(working, @"^Delivers\s+an\s+attack\s+with\s+a\s+potency\s+of\s+(?<potency>\d+(?:\.\d+)?)\.?$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return $"发动攻击，威力为{m.Groups["potency"].Value}。";
        }

        m = Regex.Match(working, @"^Additional\s+Effect\s*:\s*Grants\s+(?<status>.+?)\.?$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return $"追加效果：获得 {CleanStatusName(m.Groups["status"].Value)}。";
        }

        m = Regex.Match(working, @"^Additional\s+Effect\s*:\s*(?<effect>.+?)\.?$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return $"追加效果：{LocalizeKnownDescriptionFragments(CleanupDescriptionControlMarkers(m.Groups["effect"].Value))}。";
        }

        m = Regex.Match(working, @"^Combo\s+Bonus\s*:\s*(?<effect>.+?)\.?$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return $"连击加成：{LocalizeKnownDescriptionFragments(CleanupDescriptionControlMarkers(m.Groups["effect"].Value))}。";
        }

        m = Regex.Match(working, @"^Duration\s*:\s*(?<duration>\d+(?:\.\d+)?)\s*s\s*(?<rest>.*)$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var rest = CleanupDescriptionControlMarkers(m.Groups["rest"].Value);
            return string.IsNullOrWhiteSpace(rest)
                ? $"持续时间：{m.Groups["duration"].Value}秒。"
                : $"持续时间：{m.Groups["duration"].Value}秒。{LocalizeKnownDescriptionFragments(rest)}";
        }

        m = Regex.Match(working, @"^Can\s+only\s+be\s+executed\s+while\s+under\s+the\s+effect\s+of\s+(?<status>.+?)\.?$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return $"只能在 {CleanStatusName(m.Groups["status"].Value)} 效果期间发动。";
        }

        m = Regex.Match(working, @"^When\s+standing\s+within\s+the\s+bounds\s+of\s+(?<status>.+?),\s*consumes\s+a\s+stack\s+of\s+(?<consume>.+?)\s+if\s+available\.?$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return $"站在 {CleanStatusName(m.Groups["status"].Value)} 范围内时，若有 {CleanStatusName(m.Groups["consume"].Value)} 层数，则消耗1层。";
        }

        m = Regex.Match(working, @"^Grants\s+(?<status>.+?)\.?$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return $"获得 {CleanStatusName(m.Groups["status"].Value)}。";
        }

        if (Regex.IsMatch(working, @"^This\s+action\s+does\s+not\s+share\s+a\s+recast\s+timer\s+with\s+any\s+other\s+actions\.?$", RegexOptions.IgnoreCase))
        {
            return "此技能不与其他技能共享复唱时间。";
        }

        if (Regex.IsMatch(working, @"^Upon\s+execution,\s*the\s+recast\s+timer\s+for\s+this\s+action\s+will\s+be\s+applied\s+to\s+all\s+other\s+weaponskills\s+and\s+magic\s+actions\.?$", RegexOptions.IgnoreCase))
        {
            return "发动后，此技能的复唱时间会应用于所有其他战技与魔法技能。";
        }

        return LocalizeKnownDescriptionFragments(working);
    }

    private static string LocalizeKnownDescriptionFragments(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var result = CleanupDescriptionControlMarkers(text);
        result = Regex.Replace(result, @"\bDeals\b", "造成", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bDelivers\b", "发动", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bdamage\b", "伤害", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bwith\s+a\s+potency\s+of\b", "，威力为", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bAdditional\s+Effect\b", "追加效果", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bCombo\s+Bonus\b", "连击加成", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bDuration\b", "持续时间", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bGrants\b", "获得", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bCan\s+only\s+be\s+executed\s+while\s+under\s+the\s+effect\s+of\b", "只能在以下效果期间发动：", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bThis\s+action\s+does\s+not\s+share\s+a\s+recast\s+timer\s+with\s+any\s+other\s+actions\b", "此技能不与其他技能共享复唱时间", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bUpon\s+execution\b", "发动后", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\brecast\s+timer\b", "复唱时间", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bweaponskills\b", "战技", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bmagic\s+actions\b", "魔法技能", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"(?<=\d)s\b", "秒", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\s+([，。,.])", "$1");
        result = Regex.Replace(result, @"\.\s*$", "。");
        return result.Trim();
    }

    private static string LocalizeElement(string element)
    {
        return element.ToLowerInvariant() switch
        {
            "fire" => "火",
            "ice" => "冰",
            "wind" => "风",
            "earth" => "土",
            "lightning" => "雷",
            "water" => "水",
            "unaspected" => "无属性",
            _ => element,
        };
    }

    private static string CleanStatusName(string status)
    {
        var clean = CleanupDescriptionControlMarkers(status);
        clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', '.', ',', ':', ';');
        return clean;
    }

    private async Task<string> TranslateDescriptionBlockAsync(
        object translator,
        string descriptionSource,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(descriptionSource))
        {
            return string.Empty;
        }

        var attempts = new List<(string Text, string SourceLanguage, string TargetLanguage, string Context)>();
        attempts.Add((descriptionSource, "English", targetLanguage, "TooltipOverlay.DescriptionBlock"));
        attempts.Add((descriptionSource, "en", targetLanguage, "TooltipOverlay.DescriptionBlock.SourceEn"));

        if (IsChineseTarget(targetLanguage))
        {
            attempts.Add((descriptionSource, "English", "Simplified Chinese", "TooltipOverlay.DescriptionBlock.SimplifiedChinese"));
            attempts.Add((descriptionSource, "en", "zh-CN", "TooltipOverlay.DescriptionBlock.ZhCN"));
            attempts.Add((BuildStrictDescriptionPrompt(descriptionSource), "English", targetLanguage, "TooltipOverlay.DescriptionBlock.StrictPrompt"));
        }

        foreach (var attempt in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var translated = await this.TranslateWithServiceAsync(
                translator,
                attempt.Text,
                attempt.SourceLanguage,
                attempt.TargetLanguage,
                attempt.Context,
                cancellationToken).ConfigureAwait(false);

            translated = CleanTranslatedOutput(StripStrictDescriptionPromptEcho(translated));
            translated = LocalizeKnownEnglishLabels(translated);
            translated = RestoreMissingNumericTokens(descriptionSource, translated);

            if (!string.IsNullOrWhiteSpace(translated) && !LooksUntranslated(descriptionSource, translated, targetLanguage))
            {
                return translated;
            }
        }

        return await this.TranslateDescriptionLineByLineAsync(
            translator,
            descriptionSource,
            targetLanguage,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> TranslateDescriptionLineByLineAsync(
        object translator,
        string descriptionSource,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var output = new List<string>();
        foreach (var rawLine in descriptionSource.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                output.Add(string.Empty);
                continue;
            }

            var translated = await this.TranslateWithServiceAsync(
                translator,
                line,
                "English",
                targetLanguage,
                "TooltipOverlay.DescriptionLine",
                cancellationToken).ConfigureAwait(false);
            translated = CleanTranslatedOutput(translated);

            if (LooksUntranslated(line, translated, targetLanguage) && IsChineseTarget(targetLanguage))
            {
                translated = await this.TranslateWithServiceAsync(
                    translator,
                    line,
                    "en",
                    "zh-CN",
                    "TooltipOverlay.DescriptionLine.ZhCN",
                    cancellationToken).ConfigureAwait(false);
                translated = CleanTranslatedOutput(translated);
            }

            output.Add(LooksUntranslated(line, translated, targetLanguage) ? line : translated);
        }

        return CleanTranslatedOutput(string.Join("\n", output));
    }

    private static string NormalizeDescriptionBlockForTranslation(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var clean = text.Replace("\r\n", "\n").Replace('\r', '\n');
        clean = Regex.Replace(clean, @"\b(Description|Action data)\s*:\s*", string.Empty, RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"\b(Additional Effect|Combo Bonus|Duration)\s*:\s*", "\n$1: ", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"(?<=\.)\s+(?=(Additional Effect|Combo Bonus|Duration|Can only|This action|Upon execution|When standing|Consumes|Grants|Deals|Delivers|Restores)\b)", "\n", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"[ \t]{2,}", " ");
        clean = Regex.Replace(clean, @"\n{3,}", "\n\n");
        return clean.Trim();
    }

    private static string BuildStrictDescriptionPrompt(string descriptionSource)
    {
        return "Translate this FFXIV action tooltip description into Simplified Chinese. " +
               "Preserve all numbers, potency values, seconds, status names, and line breaks. " +
               "Do not summarize, do not omit any sentence, and return only the translated tooltip description.\n" +
               "---TOOLTIP DESCRIPTION---\n" + descriptionSource.Trim() + "\n---END TOOLTIP DESCRIPTION---";
    }

    private static string StripStrictDescriptionPromptEcho(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var clean = text.Replace("\r\n", "\n").Replace('\r', '\n');
        clean = Regex.Replace(clean, @"(?is)^.*?---\s*TOOLTIP DESCRIPTION\s*---", string.Empty).Trim();
        clean = Regex.Replace(clean, @"(?is)---\s*END TOOLTIP DESCRIPTION\s*---.*$", string.Empty).Trim();
        clean = Regex.Replace(clean, @"(?i)^\s*Translate this FFXIV action tooltip description.*?$", string.Empty, RegexOptions.Multiline).Trim();
        return clean;
    }


    private sealed class StructuredDescriptionSections
    {
        public List<string> MainLines { get; } = new();
        public List<string> AdditionalEffectLines { get; } = new();
        public List<string> DurationLines { get; } = new();
        public List<string> ComboBonusLines { get; } = new();
        public List<string> RequirementLines { get; } = new();
        public List<string> SpecialNoteLines { get; } = new();
        public List<string> OtherLines { get; } = new();

        public bool HasAnyStructuredContent =>
            this.MainLines.Count +
            this.AdditionalEffectLines.Count +
            this.DurationLines.Count +
            this.ComboBonusLines.Count +
            this.RequirementLines.Count +
            this.SpecialNoteLines.Count +
            this.OtherLines.Count > 0;

        public static StructuredDescriptionSections Parse(string description)
        {
            var result = new StructuredDescriptionSections();
            var normalized = NormalizeDescriptionBlockForTranslation(description);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return result;
            }

            foreach (var raw in normalized.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = CleanupDescriptionControlMarkers(raw.Trim());
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (Regex.IsMatch(line, @"^Description\s*:\s*$", RegexOptions.IgnoreCase))
                {
                    continue;
                }

                if (Regex.IsMatch(line, @"^Description\s*:\s*", RegexOptions.IgnoreCase))
                {
                    line = Regex.Replace(line, @"^Description\s*:\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        result.MainLines.Add(line);
                    }

                    continue;
                }

                if (Regex.IsMatch(line, @"^Additional\s+Effect\s*:", RegexOptions.IgnoreCase))
                {
                    result.AdditionalEffectLines.Add(line);
                    continue;
                }

                if (Regex.IsMatch(line, @"^Duration\s*:", RegexOptions.IgnoreCase))
                {
                    result.DurationLines.Add(line);
                    continue;
                }

                if (Regex.IsMatch(line, @"^Combo\s+Bonus\s*:", RegexOptions.IgnoreCase))
                {
                    result.ComboBonusLines.Add(line);
                    continue;
                }

                if (Regex.IsMatch(line, @"^Can\s+only\s+be\s+executed\b|^Cannot\s+be\s+executed\b|^Cannot\s+use\b", RegexOptions.IgnoreCase))
                {
                    result.RequirementLines.Add(line);
                    continue;
                }

                if (Regex.IsMatch(line, @"^This\s+action\b|^Upon\s+execution\b|^When\s+standing\b|^Consumes\b|^Shares\s+a\s+recast\s+timer\b|^Does\s+not\s+share\b", RegexOptions.IgnoreCase))
                {
                    result.SpecialNoteLines.Add(line);
                    continue;
                }

                if (Regex.IsMatch(line, @"^Deals\b|^Delivers\b|^Restores\b|^Extends\b|^Increases\b|^Reduces\b", RegexOptions.IgnoreCase))
                {
                    result.MainLines.Add(line);
                    continue;
                }

                if (Regex.IsMatch(line, @"^Grants\b", RegexOptions.IgnoreCase))
                {
                    result.AdditionalEffectLines.Add(line);
                    continue;
                }

                result.OtherLines.Add(line);
            }

            return result;
        }
    }

    private sealed class TooltipBodySections
    {
        public List<string> HeaderLines { get; } = new();
        public List<string> DescriptionLines { get; } = new();
        public List<string> ActionDataLines { get; } = new();
        public List<string> TrailingLines { get; } = new();
        public bool HasDescription => this.DescriptionLines.Count > 0;

        public static TooltipBodySections Parse(string body)
        {
            var sections = new TooltipBodySections();
            var mode = "header";
            foreach (var raw in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.Equals("Description:", StringComparison.OrdinalIgnoreCase))
                {
                    mode = "description";
                    continue;
                }

                if (line.StartsWith("Description:", StringComparison.OrdinalIgnoreCase))
                {
                    mode = "description";
                    var remainder = line["Description:".Length..].Trim();
                    if (!string.IsNullOrWhiteSpace(remainder))
                    {
                        sections.DescriptionLines.Add(remainder);
                    }
                    continue;
                }

                if (line.Equals("Action data:", StringComparison.OrdinalIgnoreCase))
                {
                    mode = "actiondata";
                    continue;
                }

                if (line.StartsWith("Action data:", StringComparison.OrdinalIgnoreCase))
                {
                    mode = "actiondata";
                    var remainder = line["Action data:".Length..].Trim();
                    if (!string.IsNullOrWhiteSpace(remainder))
                    {
                        sections.ActionDataLines.Add(remainder);
                    }
                    continue;
                }

                switch (mode)
                {
                    case "header":
                        sections.HeaderLines.Add(line);
                        break;
                    case "description":
                        sections.DescriptionLines.Add(line);
                        break;
                    case "actiondata":
                        sections.ActionDataLines.Add(line);
                        break;
                    default:
                        sections.TrailingLines.Add(line);
                        break;
                }
            }

            return sections;
        }
    }


    private async Task<string> TranslateBodyRobustAsync(
        object translator,
        string bodyForTranslation,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var translated = await this.TranslateWithServiceAsync(
            translator,
            bodyForTranslation,
            "English",
            targetLanguage,
            "TooltipOverlay.Body.PreserveAllNumbersAndGameTerms",
            cancellationToken).ConfigureAwait(false);

        translated = CleanTranslatedOutput(translated);
        translated = LocalizeKnownEnglishLabels(translated);

        // Google-style backends sometimes return long multiline action text unchanged,
        // while still translating short title strings. If that happens, retry by line.
        if (!LooksUntranslated(bodyForTranslation, translated, targetLanguage))
        {
            return translated;
        }

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

            var translatedLine = await this.TranslateWithServiceAsync(
                translator,
                line,
                "English",
                targetLanguage,
                "TooltipOverlay.BodyLine.PreserveNumbersAndFFXIVTerms",
                cancellationToken).ConfigureAwait(false);

            translatedLine = CleanTranslatedOutput(translatedLine);
            translatedLine = LocalizeKnownEnglishLabels(translatedLine);

            // If the backend still refuses to translate this specific line, leave the
            // original visible rather than dropping information from the tooltip.
            output.Add(LooksUntranslated(line, translatedLine, targetLanguage) ? line : translatedLine);
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

        if (trimmed.Equals("Description:", StringComparison.OrdinalIgnoreCase))
        {
            return "说明：";
        }

        var match = Regex.Match(trimmed, @"^(?<label>Skill type|Job|Category|Level|Potency|Cast time|Recast time|Range|Radius|Width/axis modifier|Maximum charges|MP cost|Primary cost|Secondary cost|Cost|CP|GP):\s*(?<value>.+)$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var label = match.Groups["label"].Value.ToLowerInvariant();
        var value = match.Groups["value"].Value.Trim();
        value = Regex.Replace(value, @"\byalms?\b", "米", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bInstant\b", "即时", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"(?<=\d)s\b", "秒", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bWeaponskill\b", "战技", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bSpell\b", "魔法", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bAbility\b", "能力", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bTrait\b", "特性", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bAffinity\b", "属性", RegexOptions.IgnoreCase);

        var zhLabel = label switch
        {
            "skill type" => "技能类型",
            "job" => "职业",
            "category" => "分类",
            "level" => "等级",
            "potency" => "威力",
            "cast time" => "咏唱时间",
            "recast time" => "复唱时间",
            "range" => "距离",
            "radius" => "范围半径",
            "width/axis modifier" => "宽度/轴向修正",
            "maximum charges" => "最大积蓄次数",
            "mp cost" => "MP消耗",
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

        // Remove any native/private UI glyphs that may survive the provider stage.
        clean = ActionTooltipHybridBuilder.CleanNativeTooltipText(clean);

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
        // Remove native-tooltip marker leftovers if a backend echoed them back.
        clean = Regex.Replace(clean, @"(?<![A-Za-z0-9])(?:H|I)(?:\s+(?:H|I))+(?![A-Za-z0-9])", " ");
        clean = Regex.Replace(clean, @"(?<![A-Za-z0-9])(?:H|I)(?![A-Za-z0-9])", " ");
        clean = Regex.Replace(clean, @"[ \t]{2,}", " ");
        clean = Regex.Replace(clean, @"\s+([,.;:!?，。；：！？])", "$1");
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
            // Localized fixed labels can add a few Chinese characters even when the
            // actual English description was returned unchanged. In that case, force
            // the line-by-line fallback instead of accepting a half-English body.
            return HasLongUnchangedEnglishLine(source, translated);
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

    private static bool HasLongUnchangedEnglishLine(string source, string translated)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translated))
        {
            return false;
        }

        foreach (var rawLine in source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length < 28)
            {
                continue;
            }

            var letters = line.Count(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));
            if (letters < 16)
            {
                continue;
            }

            // Skip fixed stat/label lines: these may intentionally preserve values.
            if (Regex.IsMatch(line, @"^(Skill type|Job|Category|Level|Potency|Cast time|Recast time|Range|Radius|MP cost|Action data|Description):", RegexOptions.IgnoreCase))
            {
                continue;
            }

            if (translated.Contains(line, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
        var pos = this.ComputeMouseFollowerOverlayPos(mouse, this.lastOverlaySize);

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

        if (this.ReadConfigBool("TooltipOverlayDebug", TooltipOverlayConfigDefaults.TooltipOverlayDebug))
        {
            this.DrawDebugInfo(payload);
        }

        // Keep the original mouse-following behavior. Once ImGui has measured the
        // auto-resized window, push only the overflowing part back into the game viewport.
        var measuredSize = ImGui.GetWindowSize();
        if (measuredSize.X > 1 && measuredSize.Y > 1)
        {
            this.lastOverlaySize = measuredSize;
            var correctedPos = this.ComputeMouseFollowerOverlayPos(mouse, measuredSize);
            if ((correctedPos - ImGui.GetWindowPos()).LengthSquared() > 0.25f)
            {
                ImGui.SetWindowPos(correctedPos, ImGuiCond.Always);
            }
        }

        ImGui.SetWindowFontScale(oldScale);
        ImGui.End();
    }

    private Vector2 ComputeMouseFollowerOverlayPos(Vector2 mouse, Vector2 overlaySize)
    {
        const float margin = 16f;
        var desired = mouse + new Vector2(28, 24);

        // In Dalamud overlays, ImGui mouse/window coordinates line up most reliably with
        // IO.DisplaySize. MainViewport.WorkSize can be different under some window/fullscreen
        // configurations, which is why v0.1.6 could still partially overflow.
        var displaySize = ImGui.GetIO().DisplaySize;
        if (displaySize.X <= 0 || displaySize.Y <= 0)
        {
            var viewport = ImGui.GetMainViewport();
            displaySize = viewport.WorkSize;
        }

        var size = overlaySize;
        if (size.X <= 1 || size.Y <= 1)
        {
            size = new Vector2(420, 260);
        }

        var min = new Vector2(margin, margin);
        var max = displaySize - size - new Vector2(margin, margin);
        if (max.X < min.X) max.X = min.X;
        if (max.Y < min.Y) max.Y = min.Y;

        desired.X = Math.Clamp(desired.X, min.X, max.X);
        desired.Y = Math.Clamp(desired.Y, min.Y, max.Y);
        return desired;
    }

    private void DrawDebugInfo(TooltipPayload payload)
    {
        ImGui.Separator();
        ImGui.TextDisabled("CN Tooltip Debug");

        var target = this.targetLanguageCodeProvider();
        var cacheState = payload.IsPending ? "pending" : "cached/displayed";
        var translatedTitleHasCjk = ContainsCjk(payload.TranslatedTitle) ? "yes" : "no";
        var translatedBodyHasCjk = ContainsCjk(payload.TranslatedBody) ? "yes" : "no";

        ImGui.TextDisabled($"Key: {payload.Key.CacheKey}");
        ImGui.TextDisabled($"Target: {target} | State: {cacheState}");
        ImGui.TextDisabled($"Original title/body chars: {payload.OriginalTitle?.Length ?? 0}/{payload.OriginalBody?.Length ?? 0}");
        ImGui.TextDisabled($"Translated title/body chars: {payload.TranslatedTitle?.Length ?? 0}/{payload.TranslatedBody?.Length ?? 0}");
        ImGui.TextDisabled($"CJK in title/body: {translatedTitleHasCjk}/{translatedBodyHasCjk}");

        var providerDebug = this.textProvider.GetDebugSummary(payload.Key);
        if (!string.IsNullOrWhiteSpace(providerDebug))
        {
            ImGui.TextDisabled(providerDebug);
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
