using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using Questionable.Model.Questing;
using System;
namespace Questionable.Model;

public enum EItemRewardType
{
    Mount,
    Minion,
    OrchestrionRoll,
    TripleTriadCard,
    FashionAccessory,
    FolkloreBook,
    UnlockLink,
    RecipeBook,
    Coffer
}

public sealed class ItemRewardDetails(Item item, ElementId elementId)
{
    public uint ItemId { get; } = item.RowId;
    public string Name { get; } = item.Name.ToDalamudString().ToString();
    public TimeSpan CastTime { get; } = TimeSpan.FromSeconds(item.CastTimeSeconds);
    public ElementId ElementId { get; } = elementId;
}

public abstract record ItemReward(ItemRewardDetails Item)
{
    public uint ItemId => Item.ItemId;
    public string Name => Item.Name;
    public ElementId ElementId => Item.ElementId;
    public TimeSpan CastTime => Item.CastTime;
    public abstract EItemRewardType Type { get; }
    // ⚠️ 這一型的鍵是 ItemAction 的「列 id」，不是其他獎勵型別用的 Action 值。
    // 照 Action 值去查會回 0 件，看起來跟「台服沒有這種道具」一模一樣。
    // 台服 7.20 實查：符合的任務獎勵寶箱共 236 件（武器箱、各職業裝備箱等）。
    internal static bool IsValidCoffer(Item item) =>
        item.ItemAction.RowId is 1085 or 388 or 367 && item.ItemUICategory.RowId is 61;

    internal static ItemReward? CreateFromItem(Item item, ElementId elementId)
    {
        // 🔴 寶箱沒有「已解鎖」的概念，IsUnlocked() 恆為 false，所以預設關
        // （Advanced.AutoRedeemCoffers），並由 RedeemRewardItems.AttemptedItems 擋無限重試。
        if (IsValidCoffer(item))
        {
            return new CofferReward(new(item, elementId));
        }

        if (item.ItemAction.Value is { } itemAction)
        {
            if (itemAction.Type is 1322)
            {
                return new MountReward(new(item, elementId), item.ItemAction.Value.Data[0]);
            }

            if (itemAction.Type is 853)
            {
                return new MinionReward(new(item, elementId), item.ItemAction.Value.Data[0]);
            }

            if (itemAction.Type is 20086)
            {
                return new FashionAccessoryReward(new(item, elementId), item.ItemAction.Value.Data[0]);
            }

            if (itemAction.Type is 4107)
            {
                return new FolkloreBookReward(new(item, elementId), (ushort)item.ItemAction.Value.Data[0]);
            }

            // 表情、面妝樣式之類的「學會就永久解鎖」道具。台服 7.20 實查有 9 件是真的任務獎勵，
            // 例：任務 68620「公主節的大聲援」→ 道具 22378「演技教材·聲援小藍」→ ItemAction 列 1550 → Data[0] 381。
            if (itemAction.Type is 2633)
            {
                return new UnlockLinkReward(new(item, elementId), (ushort)item.ItemAction.Value.Data[0]);
            }

            // 秘傳書。台服 7.20 沒有任何任務把它列為獎勵，屬前瞻相容。
            if (itemAction.Type is 2136)
            {
                return new RecipeBookReward(new(item, elementId), (ushort)item.ItemAction.Value.Data[0]);
            }
        }
        else if (item.AdditionalData.GetValueOrDefault<Orchestrion>() is { } orchestrionRoll)
        {
            return new OrchestrionRollReward(new(item, elementId), orchestrionRoll.RowId);
        }
        else if (item.AdditionalData.GetValueOrDefault<TripleTriadCard>() is { } tripleTriadCard)
        {
            return new TripleTriadCardReward(new(item, elementId), (ushort)tripleTriadCard.RowId);
        }

        return null;
    }
    public abstract bool IsUnlocked();
}

public sealed record MountReward(ItemRewardDetails Item, uint MountId)
    : ItemReward(Item)
{
    public override EItemRewardType Type => EItemRewardType.Mount;

    public override unsafe bool IsUnlocked()
    {
        return PlayerState.Instance()->IsMountUnlocked(MountId);
    }
}

public sealed record MinionReward(ItemRewardDetails Item, uint MinionId)
    : ItemReward(Item)
{
    public override EItemRewardType Type => EItemRewardType.Minion;

    public override unsafe bool IsUnlocked()
    {
        return UIState.Instance()->IsCompanionUnlocked(MinionId);
    }
}

public sealed record OrchestrionRollReward(ItemRewardDetails Item, uint OrchestrionRollId)
    : ItemReward(Item)
{
    public override EItemRewardType Type => EItemRewardType.OrchestrionRoll;

    public override unsafe bool IsUnlocked()
    {
        return PlayerState.Instance()->IsOrchestrionRollUnlocked(OrchestrionRollId);
    }
}

public sealed record TripleTriadCardReward(ItemRewardDetails Item, ushort TripleTriadCardId)
    : ItemReward(Item)
{
    public override EItemRewardType Type => EItemRewardType.TripleTriadCard;

    public override unsafe bool IsUnlocked()
    {
        return UIState.Instance()->IsTripleTriadCardUnlocked(TripleTriadCardId);
    }
}

public sealed record FashionAccessoryReward(ItemRewardDetails Item, uint AccessoryId)
    : ItemReward(Item)
{
    public override EItemRewardType Type => EItemRewardType.FashionAccessory;

    public override unsafe bool IsUnlocked()
    {
        return PlayerState.Instance()->IsOrnamentUnlocked(AccessoryId);
    }
}

public sealed record FolkloreBookReward(ItemRewardDetails Item, ushort FolkloreBookId)
    : ItemReward(Item)
{
    public override EItemRewardType Type => EItemRewardType.FolkloreBook;

    public override unsafe bool IsUnlocked()
    {
        return PlayerState.Instance()->IsFolkloreBookUnlocked(FolkloreBookId);
    }
}

public sealed record UnlockLinkReward(ItemRewardDetails Item, ushort UnlockLinkId)
    : ItemReward(Item)
{
    public override EItemRewardType Type => EItemRewardType.UnlockLink;

    public override unsafe bool IsUnlocked()
    {
        return UIState.Instance()->IsUnlockLinkUnlocked(UnlockLinkId);
    }
}

public sealed record RecipeBookReward(ItemRewardDetails Item, ushort RecipeBookId)
    : ItemReward(Item)
{
    public override EItemRewardType Type => EItemRewardType.RecipeBook;

    public override unsafe bool IsUnlocked()
    {
        return PlayerState.Instance()->IsSecretRecipeBookUnlocked(RecipeBookId);
    }
}

// 🔴 IsUnlocked() 恆為 false：只要背包裡有就會被判成「該兌換」。
// 擋住無限重試的是 RedeemRewardItems.AttemptedItems，不是這裡。
public sealed record CofferReward(ItemRewardDetails Item)
    : ItemReward(Item)
{
    public override EItemRewardType Type => EItemRewardType.Coffer;

    public override bool IsUnlocked()
    {
        return false;
    }
}
