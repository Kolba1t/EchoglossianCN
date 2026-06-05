// Copyright (c) fork author. Based on Echoglossian runtime patterns.
// Licensed under the same license terms as your Echoglossian fork.

using Echoglossian.TooltipOverlay;

namespace Echoglossian;

public partial class Echoglossian
{
    private TooltipOverlayTranslatorRuntime? cnTooltipOverlayRuntime;

    private void RegisterCnTooltipOverlayRuntime()
    {
        this.cnTooltipOverlayRuntime ??= new TooltipOverlayTranslatorRuntime(
            this.configuration,
            DManager,
            GameGuiInterface,
            PluginInterface,
            PluginLog,
            () => LangDict[LanguageInt].Code,
            () => TranslationService);

        this.cnTooltipOverlayRuntime.Start();
    }

    private void DisposeCnTooltipOverlayRuntime()
    {
        this.cnTooltipOverlayRuntime?.Dispose();
        this.cnTooltipOverlayRuntime = null;
    }
}
