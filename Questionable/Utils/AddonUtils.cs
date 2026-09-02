using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Text;
namespace Questionable.Utils;

internal static unsafe class AddonUtils
{
    public static AtkUnitBase* GetAddonById(uint addonId)
    {
        if (addonId == 0)
        {
            return null;
        }

        AtkStage* atkStage = AtkStage.Instance();
        if (atkStage == null || atkStage->RaptureAtkUnitManager == null)
        {
            return null;
        }

        return atkStage->RaptureAtkUnitManager->GetAddonById((ushort)addonId);
    }

    public static bool IsAddonReady(AtkUnitBase* addon)
    {
        return addon != null && addon->AtkValues != null;
    }

    /// <summary>
    ///     視窗名稱的<b>有界</b>讀法（<c>AtkUnitBase.Name</c> 是偏移 0x8 的 32 byte 固定長度欄位）。
    /// </summary>
    /// <remarks>
    ///     🔴 刻意<b>不用</b>產生器給的 <c>NameString</c>：那支展開成
    ///     <c>Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(...))</c>，
    ///     <b>沒有長度上限</b> —— 緩衝區裡剛好沒有結尾 0（欄位塞滿，或實例正在被回收、內容已被覆寫）
    ///     就會一路往後掃過整個結構，掃進未映射的頁面就是原生存取違規；AVE 在 .NET Core 是
    ///     corrupted-state exception，<c>try</c>/<c>catch</c> 完全接不到。
    ///     <para>
    ///         <c>addon-&gt;Name</c> 走的是 <c>[InlineArray(32)]</c> 產生的 <c>Span&lt;byte&gt;</c>
    ///         （<c>Length</c> 恆為 32），對它取 <c>IndexOf(0)</c> 就把讀取夾在那 32 個 byte 之內；
    ///         找不到 0 就整段當名字用。
    ///     </para>
    /// </remarks>
    public static string ReadAddonName(AtkUnitBase* addon)
    {
        if (addon == null)
        {
            return string.Empty;
        }

        Span<byte> span = addon->Name;
        int length = span.IndexOf((byte)0);
        if (length < 0)
        {
            length = span.Length;
        }

        return length == 0 ? string.Empty : Encoding.UTF8.GetString(span[..length]);
    }
}
