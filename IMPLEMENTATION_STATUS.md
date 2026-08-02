# 實作狀況報告 — AudioDeviceConfigurator

版本：尚未提交
目前自動化測試：56 通過／0 失敗
真機環境：Windows 11 x64、NVIDIA HDMI 音訊、ASUS VG27AQL1A

## 目前產品流程

- 透過 Windows Core Audio 選擇預設活動播放端點，或由 `--device-id` 指定端點。
- 傳統 `mmsys.cpl` 是唯一可選聲道與 Default Format 清單來源。
- UI Automation／Win32 只負責讀取控制台選項及清理本次建立的視窗。
- 無參數執行進入互動流程；聲道數與格式都只能依編號從清單選擇。
- 4 聲道固定採 Quadraphonic mask `0x33`；支援標準 2／4／6／8 聲道 mask。
- 最終摘要只有輸入 `Y` 才執行 SVCL。
- 修改前以 `/SaveDeviceFormat` 保存原始有效位元、取樣率、聲道數及 channel mask；mask 缺失則不修改。
- 依序執行 `/SetSpeakersConfig`、`/SetDefaultFormat` 並讀回驗證。
- 成功時保留新設定；命令失敗或讀回不符時還原並驗證原始設定。
- 不探測 WASAPI、不使用 EDID、不變更預設播放裝置、不播放音訊。

## 已移除且未恢復的舊推導功能

- `--monitor-id`
- 活動螢幕列舉、音訊端點／螢幕配對
- EDID／CTA-861 LPCM 與候選格式矩陣
- GPU 中繼資料
- WASAPI 格式支援探測

SVCL 僅重新用於套用使用者從控制台清單選定的單一組合，不會用來推導或擴充清單。

## CLI

| 命令 | 行為 |
|---|---|
| 無參數 | 互動設定預設播放端點 |
| `--device-id <id>` | 互動設定指定活動播放端點 |
| `--list` | 僅列出活動播放端點，不修改 |
| `--help` | 顯示說明 |
| `--monitor-id` | 未支援，視為未知參數 |

## 輸出

- 執行結果、控制台格式與喇叭組態直接輸出至終端機。
- 不建立 `Reports` 資料夾，也不產生 JSON／CSV 報告。

## 驗證狀態

- 自動化測試：56／56 通過。
- 已覆蓋 SVCL 格式解析、channel mask、`No items found`、互動取消、修改中取消 rollback、happy path、readback mismatch、成功 rollback 及 rollback 驗證失敗。
- 發布版真機切換通過：4 聲道／24-bit／96 kHz／mask `0x33`，讀回完全一致、exit 0，控制台視窗無殘留。
