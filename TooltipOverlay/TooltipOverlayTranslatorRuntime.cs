// Copyright (c) fork author. Based on Echoglossian runtime patterns.
// Licensed under the same license terms as your Echoglossian fork.

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.Numerics;

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
        if (nextKey.Equals(this.currentKey))
        {
            return;
        }

        this.currentKey = nextKey;
        this.hoverStartedUtc = DateTime.UtcNow;
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

            // Action and trait names are proper nouns in FFXIV. Machine translators often turn
            // names like "Imperator" into generic words like "Emperor", which is worse than
            // leaving the original name visible. Item names are still translated because they
            // are usually descriptive enough to be useful in Mandarin.
            var preserveTitle = source.Key.Kind != TooltipLookupKind.Item;

            var translatedTitle = string.IsNullOrWhiteSpace(source.OriginalTitle)
                ? string.Empty
                : preserveTitle
                    ? source.OriginalTitle
                    : await this.TranslateWithServiceAsync(
                        translator,
                        source.OriginalTitle,
                        "English",
                        targetLanguage,
                        "TooltipOverlay.Title",
                        cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var translatedBody = string.IsNullOrWhiteSpace(source.OriginalBody)
                ? string.Empty
                : await this.TranslateWithServiceAsync(
                    translator,
                    source.OriginalBody,
                    "English",
                    targetLanguage,
                    "TooltipOverlay.Body",
                    cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

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


    private async Task<string> TranslateWithServiceAsync(
        object translator,
        string text,
        string sourceLanguage,
        string targetLanguage,
        string context,
        CancellationToken cancellationToken)
    {
        var method = translator.GetType()
            .GetMethods()
            .Where(m => m.Name == "TranslateAsync")
            .OrderByDescending(m => m.GetParameters().Length)
            .FirstOrDefault(m => m.GetParameters().Length >= 3);

        if (method == null)
        {
            throw new MissingMethodException(translator.GetType().FullName, "TranslateAsync");
        }

        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
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

        var result = method.Invoke(translator, args);
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
        var pos = mouse + new Vector2(28, 24);
        var viewport = ImGui.GetMainViewport();
        var workMax = viewport.WorkPos + viewport.WorkSize;
        if (pos.X + maxWidth > workMax.X)
        {
            pos.X = Math.Max(viewport.WorkPos.X + 16, mouse.X - maxWidth - 24);
        }

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(bgAlpha);
        ImGui.SetNextWindowSizeConstraints(new Vector2(260, 0), new Vector2(maxWidth, float.MaxValue));

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav |
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

        ImGui.SetWindowFontScale(oldScale);
        ImGui.End();
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
