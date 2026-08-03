# AudioDeviceConfigurator

AudioDeviceConfigurator 會讀取指定 Windows 播放裝置在傳統「聲音」控制台中的可用聲道與「預設格式」選項，讓使用者從清單選擇後，透過 `svcl.exe` 套用並讀回驗證。

支援 Windows 10／11 x64。程式以 .NET 10 self-contained single-file 發布，不需要另裝 .NET Runtime 或第三方 NuGet 套件。

## 快速開始

請將下列檔案放在同一個可寫入資料夾：

- `AudioDeviceConfigurator.exe`
- `svcl.exe`（SVCL 1.28 以上）
- `test_audio.wav`（GUI 用來循環播放驗證）

```powershell
.\AudioDeviceConfigurator.exe
.\AudioDeviceConfigurator.exe --list
.\AudioDeviceConfigurator.exe --device-id '{0.0.0.00000000}.{...}'
.\AudioDeviceConfigurator.exe --cli    # 強制走互動式 CLI
```

直接雙擊 `AudioDeviceConfigurator.exe`（或不帶任何參數執行）會開啟 WPF 主畫面。CLI 是預設以外的進入點：只要帶 `--help`、`--list`、`--device-id`、`--cli` 任一參數，或 stdout 被重新導向，就會改走 CLI。

## GUI 流程

主畫面會列出 Core Audio 偵測到的所有活動播放端點。選擇端點後，會從傳統「聲音」控制台重新載入該端點的聲道數與預設格式清單。按 Apply 通過 SVCL 驗證切換成功後，GUI 會立即以 WASAPI Shared Mode 對所選端點循環播放 `test_audio.wav`，讓使用者即時聽到新格式的效果。

- 套用期間 Apply、Play、Stop 會鎖定。
- 只有在切換驗證成功且未播放時，Play 才會啟用。
- Stop 會釋放 WASAPI 串流；Apply 會先自動停止正在播放的串流。
- 關閉視窗會自動停止播放並釋放 WASAPI 資源。
- 播放錯誤只會顯示在狀態列，**不會**回滾已驗證成功的音訊設定。
- 執行期間開啟的「聲音」、「內容」、「喇叭設定」視窗會被自動關閉。

## CLI 流程

1. 透過 Windows Core Audio 列舉活動播放裝置。
2. 開啟傳統「聲音」控制台，唯讀取得喇叭組態與「預設格式」選項。
3. 顯示去重後的聲道數與可解析格式；控制台是唯一選項來源，不會由 EDID 或 WASAPI 補項。
4. 依序選擇聲道數與音訊格式，並顯示最終摘要。
5. 顯示 `[Y/n]` 確認提示；直接按 Enter 會採用預設值 `Y`，輸入其他值則在修改前取消。
6. 先以 `/SaveDeviceFormat` 保存原始格式與 channel mask。無法取得原始 mask 時，在任何修改前停止。
7. 依序執行 `/SetSpeakersConfig` 與 `/SetDefaultFormat`，等待 500 毫秒後讀回。
8. 聲道數、有效位元深度、取樣率及 channel mask 全部一致才算成功。
9. 若部分切換失敗或讀回不符，自動還原並驗證原始喇叭組態與格式。

4 聲道固定使用 Quadraphonic mask `0x33`。標準對應為：2=`0x3`、4=`0x33`、6=`0x3f`、8=`0x63f`。

## 命令列參數

```text
AudioDeviceConfigurator [options]

（無參數）            開啟 WPF GUI。
--cli                 強制走互動式 CLI（在已有 --list / --device-id / --help 時也是預設）。
--device-id <id>      依 Endpoint ID 設定指定活動播放裝置。
--list                列出活動播放裝置後結束，不變更設定。
--help, -h            顯示完整說明後結束。
```

## 副作用與安全行為

- 會短暫開啟傳統「聲音」控制台、喇叭設定頁及播放裝置「內容」視窗。
- 確認後會永久變更所選播放裝置的喇叭組態與預設格式。
- 不會變更 Windows 預設播放裝置。
- 只會關閉本次建立的控制台視窗，不會關閉重用的既有視窗或其他應用程式。
- 必須在已登入、未鎖定的互動式 Windows 桌面執行。
- 執行期間請勿操作程式正在讀取的控制台視窗。
- 若無法驗證還原結果，會回傳 exit code 2，並警告設定可能未完整還原。

## 結束碼

可在 PowerShell 執行後查看：

```powershell
$LASTEXITCODE
```

| 代碼 | 意義 |
|---:|---|
| `0` | **PASS** — 所選設定已套用且讀回一致。 |
| `1` | **FAIL** — 套用或讀回失敗，但原始設定已還原並驗證。 |
| `2` | **ERROR** — 裝置探索、SVCL 或還原失敗。 |
| `3` | **CANCELLED** — 在變更設定前取消。 |
| `4` | **N/A** — 沒有可選的控制台聲道或格式。 |

## 限制

- `--device-id` 會原樣傳給 SVCL，並先以 `/SaveDeviceFormat` 探測；失敗時不會改用其他裝置。
- 無法解析的控制台格式文字仍會顯示，但不能送給 SVCL 套用。
- 每次只讀取及設定一個播放裝置。
- GUI 播放前會將 `test_audio.wav` 轉換成所選端點的 WASAPI mix format。