using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using GatheringPathRenderer.Updater;

namespace GatheringPathRenderer.Windows;

internal sealed class ConfigWindow : Window
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Configuration _configuration;
    private readonly PathDownloader _pathDownloader;
    private readonly RendererPlugin _plugin;

#if !DEBUG
    private bool _overwriteModified;
#endif

    public ConfigWindow(IDalamudPluginInterface pluginInterface, Configuration configuration,
        PathDownloader pathDownloader, RendererPlugin plugin)
        : base("Gathering Path Config", ImGuiWindowFlags.AlwaysAutoResize)
    {
        _pluginInterface = pluginInterface;
        _configuration = configuration;
        _pathDownloader = pathDownloader;
        _plugin = plugin;

        AllowPinning = false;
        AllowClickthrough = false;
    }

    public override void Draw()
    {
        string authorName = _configuration.AuthorName;
        if (ImGui.InputText("Author name for new files", ref authorName, 256))
        {
            _configuration.AuthorName = authorName;
            Save();
        }

        ImGui.Separator();
        DrawPathData();
    }

    /// <summary>
    /// 路徑資料的下載區。
    /// <para>
    /// 🔴 採集路徑的 json <b>不隨外掛出貨</b>，Release 版的
    /// <c>PathsDirectory</c> 啟動時只是個空目錄 —— 沒有這顆按鈕，使用者裝了外掛
    /// 什麼都畫不出來，而且不會有任何錯誤訊息（載入 0 個位置是「成功」）。
    /// </para>
    /// <para>
    /// 📌 UI 判準：要隨時掃視的（本機有幾個檔／清單有幾個檔／失敗幾個）直接放列上，
    /// 起疑才查的（目標資料夾、來源網址、失敗的檔名）放 tooltip 或摺疊區。
    /// 「不知道」（還沒抓過清單）用灰字的 <c>?</c> 呈現，<b>不要畫成 0</b> ——
    /// 0 會被讀成「已確認遠端沒有資料」，那是完全不同的一件事。
    /// </para>
    /// </summary>
    private void DrawPathData()
    {
        ImGui.TextUnformatted("Gathering path data");

        int localCount = _plugin.GatheringLocations.Count;
        if (localCount == 0)
        {
            // 「沒有資料」必須在列上就看得見 —— 把它藏進 tooltip 的話，使用者看到的是
            // 一個安靜的空疊加層，會以為外掛壞了而不是「還沒下載資料」。
            ImGui.TextColored(ImGuiColors.DalamudYellow, "本機已載入：0 個位置檔（什麼都不會畫）");
        }
        else
        {
            ImGui.TextUnformatted($"本機已載入：{localCount} 個位置檔");
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(PathsDirectoryHint());

        ImGui.SameLine();
        int indexCount = _pathDownloader.IndexCount;
        if (indexCount < 0)
        {
            // 還沒抓過清單 ⇒ 遠端有幾個檔是「不知道」。畫成 0 會誤導成「遠端沒東西」。
            ImGui.TextColored(ImGuiColors.DalamudGrey, "／清單：?");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("這一輪還沒去取過清單，所以不知道來源有幾個檔。\n按下面的按鈕才會知道。");
        }
        else
        {
            ImGui.TextUnformatted($"／清單：{indexCount} 個");
        }

        bool running = _pathDownloader.IsRunning;

#if DEBUG
        // DEBUG 版的 PathsDirectory 指向方案裡的 GatheringPaths 原始碼資料夾。
        // 對那裡下載＝拿 origin 的內容覆寫工作樹（而且 blob 是 LF、工作樹是 CRLF，
        // 會製造出整批假的 git 變更），還可能蓋掉還沒 commit 的編輯成果。
        // 這個功能本來就只為 Release 使用者而存在，所以在 DEBUG 直接關掉。
        using (ImRaii.Disabled(true))
            ImGui.Button("下載路徑資料");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("DEBUG 版直接讀方案裡的 GatheringPaths 資料夾，不需要也不應該下載"
                             + "（會覆寫你的工作樹）。");
#else
        using (ImRaii.Disabled(running))
        {
            if (ImGui.Button(_overwriteModified ? "下載並覆蓋本機修改" : "下載缺少的路徑資料"))
                _pathDownloader.Start(_overwriteModified);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"從 {PathDownloader.PathRepoBaseUrl} 取得清單，"
                             + "再把本機沒有的檔抓下來。\n"
                             + PathsDirectoryHint());

        ImGui.SameLine();

        // 只是這一次操作的選項，刻意不寫進設定檔 —— 每次開視窗的預設值永遠是「不覆蓋」。
        ImGui.Checkbox("覆蓋本機已修改的檔", ref _overwriteModified);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("預設只下載本機沒有的檔。\n"
                             + "本機存在但內容與清單不符的檔可能是你自己編輯過的，"
                             + "也可能只是版本舊了，外掛分不出來，所以預設不動它。\n"
                             + "勾起來才會一併覆蓋。");
#endif

        DrawProgress(running);
    }

    private void DrawProgress(bool running)
    {
        PathDownloader.Phase phase = _pathDownloader.CurrentPhase;
        if (phase == PathDownloader.Phase.Idle)
            return;

        switch (phase)
        {
            case PathDownloader.Phase.FetchingIndex:
                ImGui.TextUnformatted("取得清單中…");
                break;

            case PathDownloader.Phase.Scanning:
                ImGui.TextUnformatted("比對本機檔案中…");
                break;

            case PathDownloader.Phase.Downloading:
            {
                int total = _pathDownloader.Total;
                int processed = _pathDownloader.Processed;
                float fraction = total > 0 ? (float)processed / total : 0f;
                ImGui.ProgressBar(fraction, new Vector2(260, 0), $"{processed} / {total}");
                break;
            }

            case PathDownloader.Phase.Done:
                ImGui.TextColored(_pathDownloader.Failed > 0 ? ImGuiColors.DalamudYellow : ImGuiColors.HealerGreen,
                    _pathDownloader.StatusText);
                break;

            case PathDownloader.Phase.Failed:
                ImGui.TextColored(ImGuiColors.DalamudRed, _pathDownloader.StatusText);
                break;
        }

        if (running)
            return;

        int failed = _pathDownloader.Failed;
        if (failed == 0)
            return;

        // 失敗的檔名可能有上百個，攤在 AlwaysAutoResize 的視窗上會把視窗撐爆。
        // 列上只放「有幾個失敗」（那是要能隨時掃視的），細節收在摺疊區，
        // 完整清單另外以 Information 等級寫進 log（使用者跑 LogLevel 1）。
        if (ImGui.CollapsingHeader($"失敗的 {failed} 個檔###GatheringPathFailures"))
        {
            List<string> failures = _pathDownloader.SnapshotFailures();
            int shown = Math.Min(failures.Count, 15);
            for (int i = 0; i < shown; i++)
                ImGui.TextUnformatted(failures[i]);

            if (failures.Count > shown)
                ImGui.TextUnformatted($"…還有 {failures.Count - shown} 個，完整清單在 dalamud.log");
        }
    }

    private string PathsDirectoryHint()
    {
        try
        {
            return $"目標資料夾：{_plugin.PathsDirectory.FullName}";
        }
        catch (Exception e)
        {
            return $"目標資料夾解析失敗：{e.Message}";
        }
    }

    private void Save() => _pluginInterface.SavePluginConfig(_configuration);
}
