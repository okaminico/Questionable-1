using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons.ExcelServices;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using Lumina.Excel.Sheets;
using Questionable.Controller;
using Questionable.Data;
using Questionable.Model.Questing;
using System;
using System.Collections.Generic;
using System.Linq;
using GrandCompany = FFXIVClientStructs.FFXIV.Client.UI.Agent.GrandCompany;

namespace Questionable.Windows.ConfigComponents;

internal sealed class GeneralConfigComponent : ConfigComponent
{
    private static readonly List<(uint Id, string Name)> DefaultMounts = [(0, "Mount Roulette".Loc())];
    private static readonly List<(Job ClassJob, string Name)> DefaultClassJobs = [(Job.ADV, "Auto (highest level/item level)".Loc())];

    private readonly Job[] _classJobIds;
    private readonly string[] _classJobNames;
    private readonly Job[] _craftJobIds;
    private readonly string[] _craftJobNames;
    private readonly Job[] _gatherJobIds;
    private readonly string[] _gatherJobNames;

    private readonly string[] _grandCompanyNames =
        ["None (manually pick quest)".Loc(), "Maelstrom".Loc(), "Twin Adder".Loc(), "Immortal Flames".Loc()];

    private static readonly Dictionary<Configuration.ECombatModule, string> CombatModuleNames = new()
    {
        [Configuration.ECombatModule.None] = "None".Loc(),
    };

    private static readonly Dictionary<Configuration.EGearsetUpdateSource, string> GearsetUpdateSourceNames = new()
    {
        [Configuration.EGearsetUpdateSource.Vanilla] = "Vanilla".Loc(),
    };

    private readonly uint[] _mountIds;
    private readonly string[] _mountNames;

    private readonly QuestRegistry _questRegistry;
    private readonly TerritoryData _territoryData;

    public GeneralConfigComponent(
        IDalamudPluginInterface pluginInterface,
        Configuration configuration,
        IDataManager dataManager,
        ClassJobUtils classJobUtils,
        QuestRegistry questRegistry,
        TerritoryData territoryData)
        : base(pluginInterface, configuration)
    {
        _questRegistry = questRegistry;
        _territoryData = territoryData;

        List<(uint MountId, string Name)> mounts = dataManager.GetExcelSheet<Mount>()
            .Where(x => x is { RowId: > 0, Icon: > 0 })
            .Select(x => (MountId: x.RowId, Name: x.Singular.ToString()))
            .Where(x => !string.IsNullOrEmpty(x.Name))
            .OrderBy(x => x.Name)
            .ToList();
        _mountIds = DefaultMounts.Select(x => x.Id).Concat(mounts.Select(x => x.MountId)).ToArray();
        _mountNames = DefaultMounts.Select(x => x.Name).Concat(mounts.Select(x => x.Name)).ToArray();

        List<Job> sortedClassJobs = classJobUtils.SortedClassJobs.Select(x => x.ClassJob).ToList();
        List<Job> classJobs = Enum.GetValues<Job>()
            .Where(x => x != Job.ADV)
            .Where(x => !x.IsCrafter() && !x.IsGatherer())
            .Where(x => !x.IsClass())
            .OrderBy(x => sortedClassJobs.IndexOf(x))
            .ToList();
        _classJobIds = DefaultClassJobs.Select(x => x.ClassJob).Concat(classJobs).ToArray();
        _classJobNames = DefaultClassJobs.Select(x => x.Name).Concat(classJobs.Select(x => x.ToFriendlyString())).ToArray();

        List<Job> craftJobs = Enum.GetValues<Job>()
            .Where(x => x != Job.ADV)
            .Where(x => x.IsCrafter())
            .OrderBy(x => sortedClassJobs.IndexOf(x))
            .ToList();
        _craftJobIds = craftJobs.ToArray();
        _craftJobNames = craftJobs.Select(x => x.ToFriendlyString()).ToArray();

        List<Job> gatherJobs = Enum.GetValues<Job>()
            .Where(x => x != Job.ADV)
            .Where(x => x == Job.MIN || x == Job.BTN)
            .OrderBy(x => sortedClassJobs.IndexOf(x))
            .ToList();
        _gatherJobIds = gatherJobs.ToArray();
        _gatherJobNames = gatherJobs.Select(x => x.ToFriendlyString()).ToArray();
    }

    public override void DrawTab()
    {
        using var tab = ImRaii.TabItem("General###General");
        if (!tab)
        {
            return;
        }


        Configuration.ECombatModule combatModule = Configuration.General.CombatModule;
        if (ImGuiEx.EnumCombo("Preferred Combat Module".Loc(), ref combatModule, names: CombatModuleNames))
        {
            Configuration.General.CombatModule = combatModule;
            Save();
        }

        int selectedMount = Array.FindIndex(_mountIds, x => x == Configuration.General.MountId);
        if (selectedMount == -1)
        {
            selectedMount = 0;
            Configuration.General.MountId = _mountIds[selectedMount];
            Save();
        }

        if (ImGui.Combo("Preferred Mount".Loc(), ref selectedMount, _mountNames, _mountNames.Length))
        {
            Configuration.General.MountId = _mountIds[selectedMount];
            Save();
        }

        int grandCompany = (int)Configuration.General.GrandCompany;
        if (ImGui.Combo("Preferred Grand Company".Loc(), ref grandCompany, _grandCompanyNames,
            _grandCompanyNames.Length))
        {
            Configuration.General.GrandCompany = (GrandCompany)grandCompany;
            Save();
        }

        int combatJob = Array.IndexOf(_classJobIds, Configuration.General.CombatJob);
        if (combatJob == -1)
        {
            Configuration.General.CombatJob = Job.ADV;
            Save();

            combatJob = 0;
        }

        if (ImGui.Combo("Preferred Combat Job".Loc(), ref combatJob, _classJobNames, _classJobNames.Length))
        {
            Configuration.General.CombatJob = _classJobIds[combatJob];
            Save();
        }


        int craftingJob = Array.IndexOf(_craftJobIds, Configuration.General.CraftingJob);
        if (craftingJob == -1)
        {
            Configuration.General.CraftingJob = Job.CRP;
            Save();

            craftingJob = 8;
        }

        if (ImGui.Combo("Preferred Crafting Job".Loc(), ref craftingJob, _craftJobNames, _craftJobNames.Length))
        {
            Configuration.General.CraftingJob = _craftJobIds[craftingJob];
            Save();
        }


        int gatherJob = Array.IndexOf(_gatherJobIds, Configuration.General.GatheringJob);
        if (gatherJob == -1)
        {
            Configuration.General.GatheringJob = Job.MIN;
            Save();

            gatherJob = 16;
        }

        if (ImGui.Combo("Preferred Gathering Job".Loc(), ref gatherJob, _gatherJobNames, _gatherJobNames.Length))
        {
            Configuration.General.GatheringJob = _gatherJobIds[gatherJob];
            Save();
        }

        Configuration.EGearsetUpdateSource gearsetSource = Configuration.General.GearsetUpdateSource;
        if (ImGuiEx.EnumCombo("Preferred Gear Upgrade Source".Loc(), ref gearsetSource, names: GearsetUpdateSourceNames))
        {
            Configuration.General.GearsetUpdateSource = gearsetSource;
            Save();
        }

        ImGui.Separator();
        ImGui.Text("UI".Loc());
        using (ImRaii.PushIndent())
        {
            bool hideInAllInstances = Configuration.General.HideInAllInstances;
            if (ImGui.Checkbox("Hide quest window in all instanced duties".Loc(), ref hideInAllInstances))
            {
                Configuration.General.HideInAllInstances = hideInAllInstances;
                Save();
            }

            bool useEscToCancelQuesting = Configuration.General.UseEscToCancelQuesting;
            if (ImGui.Checkbox("Use ESC to cancel questing/movement".Loc(), ref useEscToCancelQuesting))
            {
                Configuration.General.UseEscToCancelQuesting = useEscToCancelQuesting;
                Save();
            }

            bool showIncompleteSeasonalEvents = Configuration.General.ShowIncompleteSeasonalEvents;
            if (ImGui.Checkbox("Show details for incomplete seasonal events".Loc(), ref showIncompleteSeasonalEvents))
            {
                Configuration.General.ShowIncompleteSeasonalEvents = showIncompleteSeasonalEvents;
                Save();
            }

            bool hideSponsorButton = Configuration.General.HideSponsorButton;
            if (ImGui.Checkbox("Hide Sponsor button".Loc(), ref hideSponsorButton))
            {
                Configuration.General.HideSponsorButton = hideSponsorButton;
                Save();
            }
        }

#if REPORTING
        ImGui.Separator();
        ImGui.Text("Bug Report".Loc());
        using (ImRaii.PushIndent())
        {
            bool reportOptOut = Configuration.General.ReportsDisabled;
            if (ImGui.Checkbox("Opt out of bug reports".Loc(), ref reportOptOut))
            {
                Configuration.General.ReportsDisabled = reportOptOut;
                Configuration.General.DismissedReportWarning = true;
                Save();
            }
            bool dismissedReportWarning = Configuration.General.DismissedReportWarning;
            if (ImGui.Checkbox("Hide Report warning".Loc(), ref dismissedReportWarning))
            {
                Configuration.General.DismissedReportWarning = dismissedReportWarning;
                Save();
            }
            if (!reportOptOut)
            {
                string reportMessage = Configuration.General.ReportMessage;
                if (ImGui.InputText("Report message".Loc(), ref reportMessage, 256))
                {
                    Configuration.General.ReportMessage = reportMessage;
                    Save();
                }
            }
        }
#endif

        ImGui.Separator();
        ImGui.Text("Questing".Loc());
        using (ImRaii.PushIndent())
        {
            bool configureTextAdvance = Configuration.General.ConfigureTextAdvance;
            if (ImGui.Checkbox("Automatically configure TextAdvance with the recommended settings".Loc(),
                ref configureTextAdvance))
            {
                Configuration.General.ConfigureTextAdvance = configureTextAdvance;
                Save();
            }
            if (configureTextAdvance)
            {
                using (ImRaii.PushIndent())
                {
                    bool dontSkipCutscenes = Configuration.General.DontSkipCutscenes;
                    if (ImGui.Checkbox("but don't skip cutscenes or dialogue".Loc(), ref dontSkipCutscenes))
                    {
                        Configuration.General.DontSkipCutscenes = dontSkipCutscenes;
                        Save();
                    }
                }
            }

            bool skipLowPriorityInstances = Configuration.General.SkipLowPriorityDuties;
            if (ImGui.Checkbox("Unlock certain optional dungeons and raids (instead of waiting for completion)".Loc(), ref skipLowPriorityInstances))
            {
                Configuration.General.SkipLowPriorityDuties = skipLowPriorityInstances;
                Save();
            }

            ImGui.SameLine();
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.TextDisabled(FontAwesomeIcon.InfoCircle.ToIconString());
            }

            if (ImGui.IsItemHovered())
            {
                using (ImRaii.Tooltip())
                {
                    ImGui.Text("Questionable automatically picks up some optional quests (e.g. for aether currents, or the ARR alliance raids).".Loc());
                    ImGui.Text("If this setting is enabled, Questionable will continue with other quests, instead of waiting for manual completion of the duty.".Loc());

                    ImGui.Separator();
                    ImGui.Text("This affects the following dungeons and raids:".Loc());
                    foreach((uint ContentFinderConditionId, ElementId QuestId, int Sequence) lowPriorityCfc in _questRegistry.LowPriorityContentFinderConditionQuests)
                    {
                        if (_territoryData.TryGetContentFinderCondition(lowPriorityCfc.ContentFinderConditionId, out TerritoryData.ContentFinderConditionData? cfcData))
                        {
                            ImGui.BulletText($"{cfcData.Name}");
                        }
                    }
                }
            }

            bool useTickets = Configuration.General.UseTickets;
            if (ImGui.Checkbox("Use aetheryte tickets where available".Loc(), ref useTickets))
            {
                Configuration.General.UseTickets = useTickets;
                Save();
            }

            if (ImGui.IsItemHovered())
            {
                using (ImRaii.Tooltip())
                {
                    ImGui.Text("Ideally this should be set in the in-game Teleport settings, but is provided here for convenience.".Loc());
                }
            }

            ImGui.Spacing();
            bool autoStepRefreshEnabled = Configuration.General.AutoStepRefreshEnabled;
            if (ImGui.Checkbox("Automatically refresh quest steps when stuck (WIP see tooltip)".Loc(), ref autoStepRefreshEnabled))
            {
                Configuration.General.AutoStepRefreshEnabled = autoStepRefreshEnabled;
                Save();
            }

            ImGui.SameLine();
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.TextDisabled(FontAwesomeIcon.InfoCircle.ToIconString());
            }

            if (ImGui.IsItemHovered())
            {
                using (ImRaii.Tooltip())
                {
                    ImGui.Text("Questionable will automatically refresh a quest step if it appears to be stuck after the configured delay.".Loc());
                    ImGui.Text("This helps resume automated quest completion when interruptions occur.".Loc());
                    ImGui.Text("WIP feature, rather than remove it, this is a warning that it isn't fully complete.".Loc());
                }
            }

            using (ImRaii.Disabled(!autoStepRefreshEnabled))
            {
                ImGui.Indent();
                int autoStepRefreshDelay = Configuration.General.AutoStepRefreshDelaySeconds;
                ImGui.SetNextItemWidth(150f);
                if (ImGui.SliderInt("Refresh delay (seconds)".Loc(), ref autoStepRefreshDelay, 30, 180))
                {
                    Configuration.General.AutoStepRefreshDelaySeconds = autoStepRefreshDelay;
                    Save();
                }

                ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1.0f),
                    "Quest steps will refresh automatically after ?? seconds if no progress is made.".Loc(autoStepRefreshDelay));

                ImGui.Spacing();
                bool teleportOnRepeatedInterruption = Configuration.General.TeleportToAetheryteOnRepeatedInterruption;
                if (ImGui.Checkbox("Teleport to nearest aetheryte after 3 repeated interruptions (WIP see tooltip)".Loc(), ref teleportOnRepeatedInterruption))
                {
                    Configuration.General.TeleportToAetheryteOnRepeatedInterruption = teleportOnRepeatedInterruption;
                    Save();
                }

                ImGui.SameLine();
                using (ImRaii.PushFont(UiBuilder.IconFont))
                {
                    ImGui.TextDisabled(FontAwesomeIcon.InfoCircle.ToIconString());
                }

                if (ImGui.IsItemHovered())
                {
                    using (ImRaii.Tooltip())
                    {
                        ImGui.Text("If the same step keeps interrupting and retrying 3 times in a row (e.g. an interaction keeps failing), Questionable will teleport to the nearest unlocked large aetheryte in the current zone and replan the route from there, instead of waiting for the delay above.".Loc());
                        ImGui.Text("Does nothing while in combat, or if no large aetheryte in the current zone is unlocked.".Loc());
                    }
                }

                ImGui.Unindent();
            }
        }
    }
}
