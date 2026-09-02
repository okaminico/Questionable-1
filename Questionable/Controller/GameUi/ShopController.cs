using Dalamud.Plugin.Services;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;
using Questionable.Controller.GameUi.Shop;
using Questionable.Controller.GameUi.Shop.Model;
using Questionable.Model.Questing;
using Questionable.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
namespace Questionable.Controller.GameUi;

// AddonMaster.Shop isn't available in the ECommons version pinned for this API level;
// this mirrors ECommons' own implementation (UIHelpers/AddonMasterImplementations/Shop.cs)
// directly against the raw AtkValues, which is stable across API levels since it just
// reads fixed indices out of the "Shop" addon's own UI data.
internal readonly struct ShopItemInfo
{
    public required uint ItemId { get; init; }
    public required uint CostAmount { get; init; }
    public required int Index { get; init; }

    public unsafe void Select(AtkUnitBase* shop, int amount = 1)
    {
        Callback.Fire(shop, true, 0, Index, amount);
    }
}

internal static class ShopAddonReader
{
    /// <remarks>
    /// 🔴 原本三個索引(<c>[2]</c>／<c>[441 + i]</c>／<c>[75 + i]</c>)全部裸讀:
    /// <c>AtkUnitBase.AtkValues</c> 是指標欄位,addon 剛 setup／正在拆解時是 null,
    /// 長度另存在 <c>AtkValuesCount</c>。裸讀 null ＝從位址 <c>index * 0x10</c> 讀,
    /// 長度不足 ＝讀陣列後方的堆積垃圾。前者是 AccessViolationException(corrupted-state
    /// exception,<c>try</c>/<c>catch</c> 攔不到),後者更糟 —— 垃圾會變成
    /// <see cref="ShopItemInfo.ItemId"/> 被拿去<b>買東西</b>。
    /// <para>而且 <c>numEntries</c> 自己就是從同一個未驗證的陣列讀出來的,
    /// 它是垃圾時迴圈次數也跟著是垃圾 ⇒ 上界必須另外夾。</para>
    /// <para>失敗語意:回空陣列。兩個呼叫端本來就有
    /// <c>shopItems.Length == 0</c>／<c>Position &lt; shopItems.Length</c> 的路徑,
    /// 所以讀得到時行為一字不改,讀不到時是「這一幀沒有商品」而不是誤買。</para>
    /// </remarks>
    public static unsafe ShopItemInfo[] ReadShopItems(AtkUnitBase* addon)
    {
        List<ShopItemInfo> items = [];
        if (addon == null || addon->AtkValues == null)
        {
            return [];
        }

        int valueCount = addon->AtkValuesCount;
        uint numEntries = AtkValueAdapter.ReadUInt(addon, 2);
        for(int i = 0; i < numEntries; ++i)
        {
            // 兩條索引都要在界內才算數:少驗一條就等於沒驗。
            if (441 + i >= valueCount || 75 + i >= valueCount)
            {
                break;
            }

            uint itemId = addon->AtkValues[441 + i].UInt;
            if (itemId == 0)
            {
                continue;
            }

            uint costAmount = addon->AtkValues[75 + i].UInt;
            items.Add(new ShopItemInfo { ItemId = itemId, CostAmount = costAmount, Index = i });
        }

        return [.. items];
    }
}

internal sealed class ShopController : IDisposable, IShopWindow
{
    private readonly IDataManager _dataManager;
    private readonly IFramework _framework;
    private readonly IGameGuiAdapter _gameGuiAdapter;
    private readonly ILogger<ShopController> _logger;
    private readonly QuestController _questController;
    private readonly RegularShopBase _shop;

    public ShopController(QuestController questController, IGameGui gameGui, IGameGuiAdapter gameGuiAdapter, IDataManager dataManager,
        IAddonLifecycle addonLifecycle, IFramework framework, ILogger<ShopController> logger, IPluginLog pluginLog)
    {
        _questController = questController;
        _gameGuiAdapter = gameGuiAdapter;
        _dataManager = dataManager;
        _framework = framework;
        _shop = new(this, "Shop", pluginLog, gameGui, addonLifecycle);
        _logger = logger;

        _framework.Update += FrameworkUpdate;
    }

    public bool IsAutoBuyEnabled => _shop.AutoBuyEnabled;

    public bool IsAwaitingYesNo
    {
        get => _shop.IsAwaitingYesNo;
        set => _shop.IsAwaitingYesNo = value;
    }

    public void Dispose()
    {
        _framework.Update -= FrameworkUpdate;
        _shop.Dispose();
    }

    public bool IsEnabled => _questController.IsRunning;
    public bool IsOpen { get; set; }

    public Vector2? Position { get; set; } // actual implementation doesn't matter, not a real window

    public int GetCurrencyCount()
    {
        return _shop.GetItemCount(1);
        // TODO: support other currencies
    }

    public unsafe void UpdateShopStock(AtkUnitBase* addon)
    {
        QuestStep? currentStep = FindCurrentStep();
        if (currentStep == null || currentStep.InteractionType != EInteractionType.PurchaseItem)
        {
            _shop.ItemForSale = null;
            return;
        }

        ShopItemInfo[] shopItems = ShopAddonReader.ReadShopItems(addon);
        if (shopItems.Length == 0)
        {
            _shop.ItemForSale = null;
            return;
        }

        _shop.ItemForSale = shopItems
            .Select((item, i) => new ItemForSale
            {
                Position = i,
                ItemId = item.ItemId,
                ItemName = _dataManager.GetExcelSheet<Item>().GetRowOrDefault(item.ItemId)?.Name.ToString() ?? string.Empty,
                Price = item.CostAmount,
                OwnedItems = (uint)_shop.GetItemCount(item.ItemId)
            })
            .FirstOrDefault(x => x.ItemId == currentStep.ItemId);
    }

    public unsafe void TriggerPurchase(AtkUnitBase* addonShop, int buyNow)
    {
        if (_shop.ItemForSale == null)
        {
            return;
        }

        ShopItemInfo[] shopItems = ShopAddonReader.ReadShopItems(addonShop);
        if (_shop.ItemForSale.Position >= 0 && _shop.ItemForSale.Position < shopItems.Length)
        {
            shopItems[_shop.ItemForSale.Position].Select(addonShop, buyNow);
        }
    }

    public void SaveExternalPluginState()
    {
    }

    public unsafe void RestoreExternalPluginState()
    {
        if (_gameGuiAdapter.TryGetAddonByName("Shop", out AtkUnitBase* addonShop))
        {
            // 🔴 FrameworkUpdate 在「沒東西要買」的分支每一幀都會走 CancelAutoPurchase() → 這裡，
            // 而 −1 正是把商店關掉的那一發 ⇒ 沒有守衛就是每幀對正在關閉的窗再送一次。
            // 另外 RegularShopBase.ShopPreFinalize 也會走到這裡(對已經在銷毀的窗送 callback)，
            // 那條由守衛的 PreFinalize 記號擋掉。
            AddonPressGuard.PressCallbackInt("Shop", addonShop, -1);
        }
    }

    private void FrameworkUpdate(IFramework framework)
    {
        if (IsOpen && _shop.ItemForSale != null)
        {
            if (_shop.PurchaseState != null)
            {
                _shop.HandleNextPurchaseStep();
            }
            else
            {
                QuestStep? currentStep = FindCurrentStep();
                if (currentStep == null || currentStep.InteractionType != EInteractionType.PurchaseItem)
                {
                    return;
                }

                int missingItems = Math.Max(0,
                    currentStep.ItemCount.GetValueOrDefault() - (int)_shop.ItemForSale.OwnedItems);
                int toPurchase = Math.Min(_shop.GetMaxItemsToPurchase(), missingItems);
                if (toPurchase > 0)
                {
                    _logger.LogDebug("Auto-buying {MissingItems} {ItemName}", missingItems, _shop.ItemForSale.ItemName);
                    _shop.StartAutoPurchase(missingItems);
                    _shop.HandleNextPurchaseStep();
                }
                else
                {
                    _shop.CancelAutoPurchase();
                }
            }
        }
    }

    private QuestStep? FindCurrentStep()
    {
        QuestController.QuestProgress? currentQuest = _questController.CurrentQuest;
        QuestSequence? currentSequence = currentQuest?.Quest.FindSequence(currentQuest.Sequence);
        return currentSequence?.FindStep(currentQuest?.Step ?? 0);
    }
}
