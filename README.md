# Questionable

自動跑任務助手：導航到任務目標、處理對話與互動，自動完成任務。

## 功能

- 依序自動執行任務：移動、對話選項、互動、戰鬥觸發等
- 支援主線／支線任務、部分部落任務（Allied Society）、採集類任務
- 採集任務可自動移動至採集點並執行採集
- 釣魚類任務可自動生成釣組並完成釣魚（需搭配 AutoHook）
- 單人副本（Trial/Duty 的單人版本）可自動完成
- 任務優先順序視窗，可調整多個可做任務的執行順序
- 任務驗證／除錯視窗，方便回報路徑資料問題

## 相依插件

**必要**：
- [vnavmesh](https://github.com/awgil/ffxiv_navmesh) — 區域內導航移動
- [Lifestream](https://github.com/NightmareXIV/Lifestream) — 城內乙太之光快速移動
- [TextAdvance](https://github.com/NightmareXIV/TextAdvance) — 自動接取／完成任務、跳過對話與過場動畫

**戰鬥模組（擇一）**：[Boss Mod (VBM)](https://github.com/awgil/ffxiv_bossmod)、
[Wrath Combo](https://github.com/PunishXIV/WrathCombo)、
[Rotation Solver Reborn](https://github.com/FFXIV-CombatReborn/RotationSolverReborn)
（不支援 Boss Mod 的衍生版本，例如 BossMod Reborn）

**選配**：[CBT](https://github.com/Jaksuhn/Automaton)（自動完成狙擊任務）、
[Pandora's Box](https://github.com/PunishXIV/PandorasBox)（自動完成 ATM）、
[Artisan](https://github.com/PunishXIV/Artisan)（涉及製作的任務）、
[AutoDuty](https://github.com/ffxivcode/AutoDuty)（需要組隊完成的副本）

## 指令

| 指令 | 功能 |
|---|---|
| `/qst` | 開啟任務視窗 |
| `/qst config` | 開啟設定視窗 |
| `/qst start` | 開始執行任務 |
| `/qst stop` | 停止執行 |
| `/qst reload` | 重新載入所有任務資料 |
| `/qst which` | 顯示以目前選取目標開頭的任務 |
| `/qst zone` | 顯示目前所在地圖可執行的任務（僅限有路徑資料、可見且未接取的任務） |

## 安裝

在 Dalamud 設定的「自訂插件庫」加入
`https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json` 並啟用，
再從插件列表安裝。

## 作者與支援

原作者 [Liza](https://github.com/carvelli)，現由 alydev、erdelf、Limiana、Censored、
ClockwiseStarr、MrGuffels、WigglyMuffin、v3rso 等人維護。
支援與討論請至 [Discord](https://discord.gg/Zzrcc8kmvy)（`#ffxiv-Questionable` 頻道）。
