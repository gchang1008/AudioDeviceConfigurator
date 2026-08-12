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

主畫面開啟時，會自動選取 Windows 當前的預設播放裝置。Speaker Channels、Sample Rate、Bit Depth 三組 Switch 從視窗開啟後就直接顯示，不需要等 Control Panel 載入。

- **三組 Switch 以三個直立欄位水平並排**（Speaker Channels／Sample Rate／Bit Depth）。常見值會保留顯示，不支援者則停用。
- **Sample Rate** 只列出 32 kHz 至 192 kHz 之間的標準值（32、44.1、48、88.2、96、176.4、192 kHz）。
- Sample Rate 與 Bit Depth 依控制台實際列出的精確組合互相限制；若選了某個取樣率但沒有對應的位元深度（反之亦然），會自動清空另一側的選取。聲道清單維持獨立。
- 右下角的 **Active** 區塊即時顯示裝置目前的聲道／取樣率／位元深度。選擇裝置後就會自動載入，每次 **Apply**（或再次 Apply）驗證成功後也會更新。若 Active 的聲道與精確 Sample Rate／Bit Depth 組合存在於控制台可用選項中，三組 Switch 會自動預填；不支援的值保持未選取，且預填不會自動執行 Apply。
- GUI 開啟期間會依 Core Audio 熱插拔通知更新 Endpoint 清單；其他 Endpoint 的變動不會中斷目前選擇或播放。若播放中的目前 Endpoint 被拔除，循環播放會進入 default-follow 模式並改由 Windows 當前預設 Endpoint 接手；之後預設裝置再次改變時，播放也會自動轉移，但清單仍維持未選取。過期的 Switch 選項會清除，不掃描預設 Endpoint 的能力，而 **Active** 會持續顯示其 SVCL 當前格式。清單未選取時，**Play** 會使用目前預設 Endpoint，播放期間 **Stop** 仍可使用。若沒有可用的預設 Endpoint，則停止播放並清除 Active。
- **Apply** 在切換進行中會鎖定，但播放中仍可使用。播放中按下 **Apply** 會先停止目前 WASAPI 串流，再套用新格式；驗證成功後會自動以新格式開始播放。
- **Play** 不要求先執行 **Apply**；只要有所選 Endpoint（或清單未選取時有 Windows 預設 Endpoint）且目前未播放就會啟用。播放期間 **Play** 會停用，**Stop** 會啟用。播放中改變任一 RadioButton 選擇時，**Apply** 仍維持啟用，所以可以直接按 Apply 套用新選擇，不需要先按 Stop。
- **Stop** 會釋放 WASAPI 串流。
- 按 Apply 通過 SVCL 驗證切換成功後，GUI 會立即以 WASAPI Shared Mode 對所選端點循環播放 `test_audio.wav`，讓使用者即時聽到新格式的效果。
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