using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using Questionable.Controller;
using Questionable.External;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
namespace Questionable.Windows.ConfigComponents;

internal sealed class PluginConfigComponent
(
    IDalamudPluginInterface pluginInterface,
    Configuration configuration,
    CombatController combatController,
    UiUtils uiUtils,
    ICommandManager commandManager,
    AutomatonIpc automatonIpc,
    PandorasBoxIpc pandorasBoxIpc) : ConfigComponent(pluginInterface, configuration)
{
    // 這裡絕對不能指國際服的外掛庫：那些庫裡的 vnavmesh／Lifestream／TextAdvance／Artisan
    // 內部名與台服版完全相同，按下去會把 API15 的版本裝進台服環境並撞同一個已安裝鍵。
    // 台服艦隊有移植版的一律指本艦隊的 feed；沒有移植版的（Rotation Solver Reborn、
    // CBT/Automaton、Pandora's Box）保留原網址。
    private const string TcRepositoryUrl =
        "https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json";

    private static readonly IReadOnlyList<PluginInfo> RequiredPlugins =
    [
        new("vnavmesh",
            "vnavmesh",
            """
            vnavmesh handles the navigation within a zone, moving
            your character to the next quest-related objective.
            """.Loc(),
            new("https://github.com/awgil/ffxiv_navmesh/"),
            new(TcRepositoryUrl)),
        new("Lifestream",
            "Lifestream",
            """
            Used to travel to aethernet shards in cities.
            """.Loc(),
            new("https://github.com/NightmareXIV/Lifestream"),
            new(TcRepositoryUrl)),
        new("TextAdvance",
            "TextAdvance",
            """
            Automatically accepts and turns in quests, skips cutscenes
            and dialogue.
            """.Loc(),
            new("https://github.com/NightmareXIV/TextAdvance"),
            new(TcRepositoryUrl))
    ];

    private static readonly ReadOnlyDictionary<Configuration.ECombatModule, PluginInfo> CombatPlugins =
        new Dictionary<Configuration.ECombatModule, PluginInfo>
        {
            {
                Configuration.ECombatModule.BossMod,
                new("Boss Mod (VBM)",
                    "BossMod",
                    string.Empty,
                    new("https://github.com/awgil/ffxiv_bossmod"),
                    new(TcRepositoryUrl))
            },
            {
                Configuration.ECombatModule.RotationSolverReborn,
                new("Rotation Solver Reborn",
                    "RotationSolver",
                    string.Empty,
                    new("https://github.com/FFXIV-CombatReborn/RotationSolverReborn"),
                    new(
                        "https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json"))
            }
        }.AsReadOnly();
    private readonly CombatController _combatController = combatController;
    private readonly ICommandManager _commandManager = commandManager;

    private readonly Configuration _configuration = configuration;
    private readonly IDalamudPluginInterface _pluginInterface = pluginInterface;

    private readonly IReadOnlyList<PluginInfo> _recommendedPlugins =
    [
        new("CBT (formerly known as Automaton)",
            "Automaton",
            """
            Automaton is a collection of automation-related tweaks.
            """.Loc(),
            new("https://github.com/Jaksuhn/Automaton"),
            new("https://puni.sh/api/repository/croizat"),
            "/cbt",
            [
                new("'Sniper no sniping' enabled".Loc(),
                    "Automatically completes sniping tasks introduced in Stormblood".Loc(),
                    () => automatonIpc.IsAutoSnipeEnabled)
            ]),
        new("Pandora's Box",
            "PandorasBox",
            """
            Pandora's Box is a collection of tweaks.
            """.Loc(),
            new("https://github.com/PunishXIV/PandorasBox"),
            new("https://puni.sh/api/plugins"),
            "/pandora",
            [
                new("'Auto Active Time Maneuver' enabled".Loc(),
                    """
                    Automatically completes active time maneuvers in
                    single player instances, trials and raids"
                    """.Loc(),
                    () => pandorasBoxIpc.IsAutoActiveTimeManeuverEnabled)
            ]),
        new("Artisan",
            "Artisan",
            """
            Automates crafting
            """.Loc(),
            new("https://github.com/PunishXIV/Artisan"),
            new(TcRepositoryUrl),
            "/artisan")
    ];
    private readonly UiUtils _uiUtils = uiUtils;

    public override void DrawTab()
    {
        using var tab = ImRaii.TabItem($"{"Dependencies".Loc()}###Plugins");
        if (!tab)
        {
            return;
        }

        Draw(out bool allRequiredInstalled);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (allRequiredInstalled)
        {
            ImGui.TextColored(ImGuiColors.ParsedGreen, "All required plugins are installed.".Loc());
        }
        else
        {
            ImGui.TextColored(ImGuiColors.DalamudRed,
                "Required plugins are missing, Questionable will not work properly.".Loc());
        }
    }

    public void Draw(out bool allRequiredInstalled)
    {
        float checklistPadding;
        using (_pluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            checklistPadding = ImGui.CalcTextSize(FontAwesomeIcon.Check.ToIconString()).X +
                               ImGui.GetStyle().ItemSpacing.X;
        }

        ImGui.Text("Questionable requires the following plugins to work:".Loc());
        allRequiredInstalled = true;
        using (ImRaii.PushIndent())
        {
            foreach(PluginInfo plugin in RequiredPlugins)
            {
                allRequiredInstalled &= DrawPlugin(plugin, checklistPadding);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text("Questionable recommends Boss Mod (VBM) for rotation/combat automation.".Loc());

        using (ImRaii.Disabled(_combatController.IsRunning))
        {
            using (ImRaii.PushIndent())
            {
                if (ImGui.RadioButton("No rotation/combat plugin (combat must be done manually)".Loc(),
                    _configuration.General.CombatModule == Configuration.ECombatModule.None))
                {
                    _configuration.General.CombatModule = Configuration.ECombatModule.None;
                    _pluginInterface.SavePluginConfig(_configuration);
                }

                allRequiredInstalled &= DrawCombatPlugin(Configuration.ECombatModule.BossMod, checklistPadding);
            }
            ImGui.Text("The following rotation/combat plugin(s) are provided for compatibility and testing purposes:".Loc());
            using (ImRaii.PushIndent())
            {
                allRequiredInstalled &=
                    DrawCombatPlugin(Configuration.ECombatModule.RotationSolverReborn, checklistPadding);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text("The following plugins are recommended, but not required:".Loc());
        using (ImRaii.PushIndent())
        {
            foreach(PluginInfo plugin in _recommendedPlugins)
            {
                DrawPlugin(plugin, checklistPadding);
            }
        }
    }

    private bool DrawPlugin(PluginInfo plugin, float checklistPadding)
    {
        using (ImRaii.PushId("plugin_" + plugin.DisplayName))
        {
            IExposedPlugin? installedPlugin = FindInstalledPlugin(plugin);
            bool isInstalled = installedPlugin != null;
            string label = plugin.DisplayName;
            if (installedPlugin != null)
            {
                label += $" v{installedPlugin.Version}";
            }

            _uiUtils.ChecklistItem(label, isInstalled);

            DrawPluginDetails(plugin, checklistPadding, isInstalled);
            return isInstalled;
        }
    }

    private bool DrawCombatPlugin(Configuration.ECombatModule combatModule, float checklistPadding)
    {
        ImGui.Spacing();

        PluginInfo plugin = CombatPlugins[combatModule];
        using (ImRaii.PushId("plugin_" + plugin.DisplayName))
        {
            IExposedPlugin? installedPlugin = FindInstalledPlugin(plugin);
            bool isInstalled = installedPlugin != null;
            string label = plugin.DisplayName;
            if (installedPlugin != null)
            {
                label += $" v{installedPlugin.Version}";
            }

            if (ImGui.RadioButton(label, _configuration.General.CombatModule == combatModule))
            {
                _configuration.General.CombatModule = combatModule;
                _pluginInterface.SavePluginConfig(_configuration);
            }

            ImGui.SameLine(0);
            using (_pluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                Vector4 iconColor = isInstalled ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed;
                FontAwesomeIcon icon = isInstalled ? FontAwesomeIcon.Check : FontAwesomeIcon.Times;

                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(iconColor, icon.ToIconString());
            }

            DrawPluginDetails(plugin, checklistPadding, isInstalled);
            return isInstalled || _configuration.General.CombatModule != combatModule;
        }
    }

    private void DrawPluginDetails(PluginInfo plugin, float checklistPadding, bool isInstalled)
    {
        using (ImRaii.PushIndent(checklistPadding))
        {
            if (!string.IsNullOrEmpty(plugin.Details))
            {
                ImGui.TextUnformatted(plugin.Details);
            }

            bool allDetailsOk = true;
            if (plugin.DetailsToCheck != null)
            {
                foreach(PluginDetailInfo detail in plugin.DetailsToCheck)
                {
                    bool detailOk = detail.Predicate();
                    allDetailsOk &= detailOk;

                    _uiUtils.ChecklistItem(detail.DisplayName, isInstalled && detailOk);
                    if (!string.IsNullOrEmpty(detail.Details))
                    {
                        using (ImRaii.PushIndent(checklistPadding))
                        {
                            ImGui.TextUnformatted(detail.Details);
                        }
                    }
                }
            }

            ImGui.Spacing();

            if (isInstalled)
            {
                if (!allDetailsOk && plugin.ConfigCommand != null && plugin.ConfigCommand.StartsWith('/'))
                {
                    if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Cog, "Open configuration".Loc()))
                    {
                        _commandManager.ProcessCommand(plugin.ConfigCommand);
                    }
                }
            }
            else
            {
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Globe, "Open Website".Loc()))
                {
                    Util.OpenLink(plugin.WebsiteUri.ToString());
                }

                ImGui.SameLine();
                if (plugin.DalamudRepositoryUri != null)
                {
                    if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Code, "Open Repository".Loc()))
                    {
                        Util.OpenLink(plugin.DalamudRepositoryUri.ToString());
                    }
                }
                else
                {
                    ImGui.AlignTextToFramePadding();
                    ImGuiComponents.HelpMarker("Available on official Dalamud Repository".Loc());
                }
            }
        }
    }

    private static bool PluginImageButton(PluginInfo plugin, float size, bool isInstalled, bool isActive)
    {
        string url = $"https://qstxiv.github.io/icons/{plugin.InternalName}.png";
        if (ThreadLoadImageHandler.TryGetTextureWrap(url, out IDalamudTextureWrap? logo))
        {
            return ImGui.ImageButton(
                logo.Handle,
                new Vector2(size.Scale(), size.Scale()),
                new Vector2(0, 0),
                new Vector2(1, 1),
                2,
                isInstalled ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed,
                isActive ? Vector4.One : new Vector4(0.5f, 0.5f, 0.5f, 1f)
            );
        }
        return false;
    }

    private IExposedPlugin? FindInstalledPlugin(PluginInfo pluginInfo)
    {
        // Our TC fleet's fork of "Boss Mod (VBM)" is FFXIV-CombatReborn/BossModReborn, whose
        // InternalName is "BossModReborn", not upstream's "BossMod" - the IPC integration
        // (BossModIpc.cs) already talks to it fine via the generic "BossMod" IPC prefix both
        // forks register under, but this installed-plugin lookup still needs to recognize it.
        return _pluginInterface.InstalledPlugins.FirstOrDefault(x =>
            (x.InternalName == pluginInfo.InternalName ||
             (pluginInfo.InternalName == "BossMod" && x.InternalName == "BossModReborn")) &&
            x.IsLoaded);
    }

    private sealed record PluginInfo
    (
        string DisplayName,
        string InternalName,
        string Details,
        Uri WebsiteUri,
        Uri? DalamudRepositoryUri,
        string? ConfigCommand = null,
        List<PluginDetailInfo>? DetailsToCheck = null);

    private sealed record PluginDetailInfo(string DisplayName, string Details, Func<bool> Predicate);
}
