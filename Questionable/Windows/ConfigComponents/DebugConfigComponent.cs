using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin;
using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using System;
namespace Questionable.Windows.ConfigComponents;

internal sealed class DebugConfigComponent(IDalamudPluginInterface pluginInterface, Configuration configuration) : ConfigComponent(pluginInterface, configuration)
{
    public override void DrawTab()
    {
        using var tab = ImRaii.TabItem($"{"Advanced".Loc()}###Debug");
        if (!tab)
        {
            return;
        }

        ImGui.TextColored(ImGuiColors.DalamudRed,
            "Enabling any option here may cause unexpected behavior. Use at your own risk.".Loc());

        ImGui.Separator();

        bool debugOverlay = Configuration.Advanced.DebugOverlay;
        if (ImGui.Checkbox("Enable debug overlay".Loc(), ref debugOverlay))
        {
            Configuration.Advanced.DebugOverlay = debugOverlay;
            Save();
        }

        using (ImRaii.Disabled(!debugOverlay))
        {
            using (ImRaii.PushIndent())
            {
                bool combatDataOverlay = Configuration.Advanced.CombatDataOverlay;
                if (ImGui.Checkbox("Enable combat data overlay".Loc(), ref combatDataOverlay))
                {
                    Configuration.Advanced.CombatDataOverlay = combatDataOverlay;
                    Save();
                }
            }
        }

        bool highlightNpc = Configuration.Advanced.HighlightSelectedNpc;
        if (ImGui.Checkbox("Highlight NPCs related to the current quest sequence".Loc(), ref highlightNpc))
        {
            Configuration.Advanced.HighlightSelectedNpc = highlightNpc;
            Save();
        }

        using (ImRaii.Disabled(!highlightNpc))
        {
            using (ImRaii.PushIndent())
            {
                string[] highlightColorNames = Enum.GetNames<ObjectHighlightColor>();
                ObjectHighlightColor[] highlightColorValues = Enum.GetValues<ObjectHighlightColor>();
                int selectedHighlightColor = Array.IndexOf(highlightColorValues, Configuration.Advanced.HighlightColor);
                ImGui.SetNextItemWidth(150f);
                if (ImGui.Combo("Highlight Color".Loc(), ref selectedHighlightColor, highlightColorNames, highlightColorNames.Length))
                {
                    Configuration.Advanced.HighlightColor = (ObjectHighlightColor)selectedHighlightColor;
                    Save();
                }
            }
        }

        bool neverFly = Configuration.Advanced.NeverFly;
        if (ImGui.Checkbox("Disable flying (even if unlocked for the zone)".Loc(), ref neverFly))
        {
            Configuration.Advanced.NeverFly = neverFly;
            Save();
        }

        bool additionalStatusInformation = Configuration.Advanced.AdditionalStatusInformation;
        if (ImGui.Checkbox("Draw additional status information".Loc(), ref additionalStatusInformation))
        {
            Configuration.Advanced.AdditionalStatusInformation = additionalStatusInformation;
            Save();
        }

        if (additionalStatusInformation)
        {
            bool showTracked = Configuration.Advanced.ShowTracked;
            bool showDailies = Configuration.Advanced.ShowDailies;
            bool showDirector = Configuration.Advanced.ShowDirector;
            bool showActionManager = Configuration.Advanced.ShowActionManager;
            bool showNewGamePlus = Configuration.Advanced.ShowNewGamePlus;
            using (ImRaii.PushIndent())
            {
                ImGui.AlignTextToFramePadding();
                if (ImGui.Checkbox("Show Tracked Quests".Loc(), ref showTracked))
                {
                    Configuration.Advanced.ShowTracked = showTracked;
                    Save();
                }
                if (ImGui.Checkbox("Show Accepted/Complete Daily Quests".Loc(), ref showDailies))
                {
                    Configuration.Advanced.ShowDailies = showDailies;
                    Save();
                }
                if (ImGui.Checkbox("Show Director info".Loc(), ref showDirector))
                {
                    Configuration.Advanced.ShowDirector = showDirector;
                    Save();
                }
                if (ImGui.Checkbox("Show Action Manager".Loc(), ref showActionManager))
                {
                    Configuration.Advanced.ShowActionManager = showActionManager;
                    Save();
                }
                if (ImGui.Checkbox("Show NG+ Chapter".Loc(), ref showNewGamePlus))
                {
                    Configuration.Advanced.ShowNewGamePlus = showNewGamePlus;
                    Save();
                }
            }
        }

        ImGui.Separator();

        ImGui.Text("AutoDuty Settings".Loc());
        using (ImRaii.PushIndent())
        {
            ImGui.AlignTextToFramePadding();
            bool disableAutoDutyBareMode = Configuration.Advanced.DisableAutoDutyBareMode;
            if (ImGui.Checkbox("Use Pre-Loop/Loop/Post-Loop settings".Loc(), ref disableAutoDutyBareMode))
            {
                Configuration.Advanced.DisableAutoDutyBareMode = disableAutoDutyBareMode;
                Save();
            }

            ImGui.SameLine();
            ImGuiComponents.HelpMarker(
                "Typically, the loop settings for AutoDuty are disabled when running dungeons with Questionable, since they can cause issues (or even shut down your PC).".Loc());
        }

        ImGui.Separator();

        ImGui.Text("Item Rewards".Loc());
        using (ImRaii.PushIndent())
        {
            bool autoRedeemCoffers = Configuration.Advanced.AutoRedeemCoffers;
            if (ImGui.Checkbox("Automatically open quest reward coffers".Loc(), ref autoRedeemCoffers))
            {
                Configuration.Advanced.AutoRedeemCoffers = autoRedeemCoffers;
                Save();
            }

            ImGui.SameLine();
            ImGuiComponents.HelpMarker(
                "Quest reward coffers (weapon/armour boxes) have no 'already unlocked' state, so Questionable cannot tell whether you meant to keep one. When enabled, any such coffer sitting in your inventory is opened the next time a quest is accepted. Each stack is only attempted once per run.".Loc());
        }

        ImGui.Separator();
        ImGui.Text("Quest/Interaction Skips".Loc());
        using (ImRaii.PushIndent())
        {
            bool skipAetherCurrents = Configuration.Advanced.SkipAetherCurrents;
            if (ImGui.Checkbox("Don't pick up aether currents/aether current quests".Loc(), ref skipAetherCurrents))
            {
                Configuration.Advanced.SkipAetherCurrents = skipAetherCurrents;
                Save();
            }

            ImGui.SameLine();
            ImGuiComponents.HelpMarker("If not done during the MSQ by Questionable, you have to manually pick up any missed aether currents/quests. There is no way to automatically pick up all missing aether currents.".Loc());

            bool skipClassJobQuests = Configuration.Advanced.SkipClassJobQuests;
            if (ImGui.Checkbox("Don't pick up class/job/role quests".Loc(), ref skipClassJobQuests))
            {
                Configuration.Advanced.SkipClassJobQuests = skipClassJobQuests;
                Save();
            }

            ImGui.SameLine();
            ImGuiComponents.HelpMarker("Class and job skills for A Realm Reborn, Heavensward and (for the Lv70 skills) Stormblood are locked behind quests. Not recommended if you plan on queueing for instances with duty finder/party finder.".Loc());

            bool skipARealmRebornHardModePrimals = Configuration.Advanced.SkipARealmRebornHardModePrimals;
            if (ImGui.Checkbox("Don't pick up ARR hard mode primal quests".Loc(), ref skipARealmRebornHardModePrimals))
            {
                Configuration.Advanced.SkipARealmRebornHardModePrimals = skipARealmRebornHardModePrimals;
                Save();
            }

            ImGui.SameLine();
            ImGuiComponents.HelpMarker("Hard mode Ifrit/Garuda/Titan are required for the Patch 2.5 quest 'Good Intentions' and to start Heavensward.".Loc());

            bool skipCrystalTowerRaids = Configuration.Advanced.SkipCrystalTowerRaids;
            if (ImGui.Checkbox("Don't pick up Crystal Tower quests".Loc(), ref skipCrystalTowerRaids))
            {
                Configuration.Advanced.SkipCrystalTowerRaids = skipCrystalTowerRaids;
                Save();
            }

            ImGui.SameLine();
            ImGuiComponents.HelpMarker("Crystal Tower raids are required for the Patch 2.55 quest 'A Time to Every Purpose' and to start Heavensward.".Loc());

            bool preventQuestCompletion = Configuration.Advanced.PreventQuestCompletion;
            if (ImGui.Checkbox("Prevent quest completion".Loc(), ref preventQuestCompletion))
            {
                Configuration.Advanced.PreventQuestCompletion = preventQuestCompletion;
                Save();
            }

            ImGui.SameLine();
            ImGuiComponents.HelpMarker("When enabled, Questionable will not attempt to turn-in and complete quests. This will do everything automatically except the final turn-in step.".Loc());

            bool namazuPreferCraft = Configuration.Advanced.NamazuPreferCraft;
            if (ImGui.Checkbox("Namazu: prefer Crafting job over Gatherer".Loc(), ref namazuPreferCraft))
            {
                Configuration.Advanced.NamazuPreferCraft = namazuPreferCraft;
                Save();
            }

            ImGui.SameLine();
            ImGuiComponents.HelpMarker("Namazu tribe quests can be done as either DoH or DoL, this lets you set that preference.".Loc());

            bool showWindowOnStart = Configuration.Advanced.ShowWindowOnStart;
            if (ImGui.Checkbox("Show window on start".Loc(), ref showWindowOnStart))
            {
                Configuration.Advanced.ShowWindowOnStart = showWindowOnStart;
                Save();
            }

            ImGui.SameLine();
            ImGuiComponents.HelpMarker("When enabled, Questionable's progress window will show when the plugin is loaded.".Loc());

            bool startMinimized = Configuration.Advanced.StartMinimized;
            if (ImGui.Checkbox("Start minimized".Loc(), ref startMinimized))
            {
                Configuration.Advanced.StartMinimized = startMinimized;
                Save();
            }

            ImGui.SameLine();
            ImGuiComponents.HelpMarker("When enabled, Questionable's progress window will be in its minimized state when loaded.".Loc());

#if DEBUG
            bool openEditor = Configuration.Advanced.OpenEditor;
            if (ImGui.Checkbox("Open editor when starting quest", ref openEditor))
            {
                Configuration.Advanced.OpenEditor = openEditor;
                Save();
            }

            ImGui.SameLine();
            ImGuiComponents.HelpMarker("When enabled, Questionable will open the path for the current quest in your default text editor.");
#endif
        }
    }
}
