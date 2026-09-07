using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Questionable.Controller;
using Questionable.Controller.Utils;
using Questionable.External;
using Questionable.Functions;
using Questionable.Windows;
using System;
namespace Questionable;

internal sealed class DalamudInitializer : IDisposable
{
    private readonly AlliedSocietyQuestFunctions _alliedSocietyQuestFunctions;
    private readonly Configuration _configuration;
    private readonly ConfigWindow _configWindow;
    private readonly IFramework _framework;
    private readonly HighlightObject _highlightObject;
    private readonly ILogger<DalamudInitializer> _logger;
    private readonly MovementController _movementController;
    private readonly NavmeshIpc _navmeshIpc;
    private readonly OneTimeSetupWindow _oneTimeSetupWindow;
    private readonly PartyWatchDog _partyWatchDog;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly QuestController _questController;
    private readonly QuestWindow _questWindow;
    private readonly IToastGui _toastGui;
    private readonly WindowSystem _windowSystem;

    public DalamudInitializer(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        QuestController questController,
        MovementController movementController,
        NavmeshIpc navmeshIpc,
        WindowSystem windowSystem,
        OneTimeSetupWindow oneTimeSetupWindow,
        QuestWindow questWindow,
        DebugOverlay debugOverlay,
        ConfigWindow configWindow,
        QuestSelectionWindow questSelectionWindow,
        QuestValidationWindow questValidationWindow,
        JournalProgressWindow journalProgressWindow,
        PriorityWindow priorityWindow,
        IToastGui toastGui,
        Configuration configuration,
        HighlightObject highlightObject,
        PartyWatchDog partyWatchDog,
        AlliedSocietyQuestFunctions alliedSocietyQuestFunctions,
        ILogger<DalamudInitializer> logger)
    {
        _pluginInterface = pluginInterface;
        _framework = framework;
        _questController = questController;
        _movementController = movementController;
        _navmeshIpc = navmeshIpc;
        _windowSystem = windowSystem;
        _oneTimeSetupWindow = oneTimeSetupWindow;
        _questWindow = questWindow;
        _configWindow = configWindow;
        _toastGui = toastGui;
        _configuration = configuration;
        _highlightObject = highlightObject;
        _partyWatchDog = partyWatchDog;
        _alliedSocietyQuestFunctions = alliedSocietyQuestFunctions;
        _logger = logger;

        _windowSystem.AddWindow(oneTimeSetupWindow);
        _windowSystem.AddWindow(questWindow);
        _windowSystem.AddWindow(configWindow);
        _windowSystem.AddWindow(debugOverlay);
        _windowSystem.AddWindow(questSelectionWindow);
        _windowSystem.AddWindow(questValidationWindow);
        _windowSystem.AddWindow(journalProgressWindow);
        _windowSystem.AddWindow(priorityWindow);

        _pluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenMainUi += ToggleQuestWindow;
        _pluginInterface.UiBuilder.OpenConfigUi += _configWindow.Toggle;
        _framework.Update += FrameworkUpdate;
        _toastGui.Toast += OnToast;
        _toastGui.ErrorToast += OnErrorToast;
        _toastGui.QuestToast += OnQuestToast;
        if (_configuration.Advanced.StartMinimized)
        {
            _questWindow.IsMinimized = true;
        }
        if (_configuration.Advanced.ShowWindowOnStart)
        {
            ToggleQuestWindow();
        }
    }

    public void Dispose()
    {
        _toastGui.QuestToast -= OnQuestToast;
        _toastGui.ErrorToast -= OnErrorToast;
        _toastGui.Toast -= OnToast;
        _framework.Update -= FrameworkUpdate;
        _pluginInterface.UiBuilder.OpenConfigUi -= _configWindow.Toggle;
        _pluginInterface.UiBuilder.OpenMainUi -= ToggleQuestWindow;
        _pluginInterface.UiBuilder.Draw -= _windowSystem.Draw;

        _windowSystem.RemoveAllWindows();
    }

    private void FrameworkUpdate(IFramework framework)
    {
        // 🔴 放在最前面：vnavmesh 移動租約的續約與逾時交回都靠這一行，而下面任何一支
        //    Update() 擲例外都會讓它之後的程式碼當幀不執行。漏續約的失效形式是「跑到一半
        //    vnavmesh 的路徑容許值忽然跳回別人的值」，而且全程零訊息。
        _navmeshIpc.UpdateLease();

        // 🔴 必須留在 _questController.Update() 之前：那一支會持著 _progressLock 判斷
        //    「還在不在尋路／移動」，而那兩個值原本是現讀 vnavmesh 的 IPC（等於在我們每幀
        //    都會拿的鎖裡面跑別的外掛的碼）。改成這裡每幀先取樣一次、鎖內只讀快照。
        //    這一行被移到 _questController.Update() 後面的話語意只會退化成「晚一幀」，
        //    仍然安全，但就不是同幀的值了。
        _movementController.RefreshNavmeshSnapshot();

        _partyWatchDog.Update();
        _questController.Update();

        try
        {
            _movementController.Update();
        }
        catch(MovementController.PathfindingFailedException)
        {
            _questController.Stop("Pathfinding failed");
        }

        // 🔴 排在最後、而且在所有鎖外面：AlliedSocietyQuestFunctions 是從 QuestController 持著
        //    _progressLock 的路徑被呼叫到的，它把要寫的記錄先收進佇列，這裡才真的寫出去。
        //    放在這裡而不是 QuestController 裡面，是因為 QuestController.Update 在「手動模式、
        //    沒在跑、視窗關著」時會提早 return，佇列會壓著不寫。
        _alliedSocietyQuestFunctions.FlushPendingLogs();
    }

    private void OnToast(ref SeString message, ref ToastOptions options, ref bool isHandled)
    {
        _logger.LogTrace("Normal Toast: {Message}", message);
    }

    private void OnErrorToast(ref SeString message, ref bool isHandled)
    {
        _logger.LogTrace("Error Toast: {Message}", message);
    }

    private void OnQuestToast(ref SeString message, ref QuestToastOptions options, ref bool isHandled)
    {
        _logger.LogTrace("Quest Toast: {Message}", message);
    }

    private void ToggleQuestWindow()
    {
        if (_configuration.IsPluginSetupComplete())
        {
            _questWindow.ToggleOrUncollapse();
        }
        else
        {
            _oneTimeSetupWindow.IsOpenAndUncollapsed = true;
        }
    }
}
