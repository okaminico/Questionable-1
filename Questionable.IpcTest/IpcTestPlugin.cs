using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Questionable.IpcTest;

// ReSharper disable once InconsistentNaming
public sealed class IpcTestPlugin : IDalamudPlugin
{
    //private readonly WindowSystem _windowSystem = new("Questionable/" + nameof(IpcTestPlugin));
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;
    private readonly IChatGui _chatGui;

    public IpcTestPlugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IChatGui chatGui)
    {
        _pluginInterface = pluginInterface;
        _commandManager = commandManager;
        _chatGui = chatGui;

        commandManager.AddHandler("/qipc", new CommandInfo(ProcessCommand));
    }

    private void ProcessCommand(string command, string arguments)
    {
        if (arguments == "stepdata")
        {
            var stepData = _pluginInterface.GetIpcSubscriber<IpcStepData?>("Questionable.GetCurrentStepData").InvokeFunc();
            _chatGui.Print(new SeStringBuilder()
                .AddUiForeground("[IPC]", 576)
                .AddText(": Type: ")
                .AddUiForeground(stepData?.InteractionType ?? "?", 61)
                .AddText(" - Pos: ")
                .AddUiForeground(stepData?.Position?.ToString("G", CultureInfo.InvariantCulture) ?? "?", 61)
                .AddText(" - Territory: ")
                .AddUiForeground(stepData?.TerritoryId.ToString() ?? "?", 61)
                .Build());
        }
        else if (arguments == "events")
        {
            var eventQuests = _pluginInterface.GetIpcSubscriber<List<string>>("Questionable.GetCurrentlyActiveEventQuests").InvokeFunc();
            _chatGui.Print(new SeStringBuilder()
                .AddUiForeground("[IPC]", 576)
                .AddText(": Quests: ")
                .AddUiForeground(string.Join(", ", eventQuests), 61)
                .Build());
        }
        else
            _chatGui.PrintError("Unknown subcommand");
    }

    public void Dispose()
    {
        _commandManager.RemoveHandler("/qipc");
    }

    [SuppressMessage("ReSharper", "ClassNeverInstantiated.Local")]
    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Local")]
    // ReSharper disable once InconsistentNaming
    // 🔴 這是 Questionable.External.QuestionableIpc.StepData 的鏡像型別（本專案刻意不參考
    //    Questionable，所以具名不到對方的型別）。Dalamud 的 CallGateChannel.ConvertObject 靠
    //    Newtonsoft 做 JSON 來回轉換，**只認成員名字**——名字對不上不會擲例外，只會留在預設值。
    //    ⇒ 每個成員名與型別都必須逐字等於對方（TerritoryId 在對方是 uint，本檔原本寫 ushort，
    //    只是因為區域 id 一直沒超過 65535 才沒出事）。對方沒有的成員可以不寫，寫了就不能寫錯。
    private sealed class IpcStepData
    {
        public required string InteractionType { get; set; }
        public required Vector3? Position { get; set; }
        public required uint TerritoryId { get; set; }
    }
}
