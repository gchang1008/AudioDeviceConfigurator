# AudioDeviceConfigurator 真機 GUI 驗收工具

此工具從外部啟動 `publish/AudioDeviceConfigurator.exe`，透過 Windows UI Automation 操作真正的 WPF GUI，並在明確確認後驗證 SVCL readback 與指定 Endpoint 的播放訊號。

它不會隨產品發布，也不會由一般 `dotnet test` 自動執行。

## 前置條件

- Windows 10／11 x64，已登入且未鎖定的互動式桌面。
- `publish` 內有 `AudioDeviceConfigurator.exe`、`svcl.exe`、`test_audio.wav`。
- 執行期間不要操作被測 GUI 或傳統 Sound Control Panel。
- 關閉其他會向同一 Endpoint 播放音訊的程式；否則 peak meter 可能只能回報 `INCONCLUSIVE`。

## 只讀盤點

```powershell
dotnet run --project tools/AudioDeviceConfigurator.Acceptance -c Release -- inspect
```

`inspect` 只啟動 GUI、列出每個 Endpoint 的 ID、聲道與格式，再關閉 GUI；不按 Apply，也不執行 SVCL setter。

## 完整驗收

```powershell
dotnet run --project tools/AudioDeviceConfigurator.Acceptance -c Release -- run `
  --endpoint-id '{0.0.0.00000000}.{...}' `
  --channels 2 `
  --format-text '24 位元，48000 Hz (錄音室品質)'
```

工具會先顯示 Endpoint、選定值及原始 SVCL readback。只有在 console 精確輸入 `APPLY` 後才會按 GUI 的 Apply；EOF 或其他輸入會回 `CANCELLED`，且不修改設定。

驗收項目：

1. GUI 套用並顯示精確驗證成功。
2. SVCL readback 的聲道、有效位元、取樣率及 channel mask 完全一致。
3. 自動播放期間指定 Endpoint peak 明顯高於 baseline。
4. 播放中 Play 無效、Stop 有效。
5. Stop 後 peak 回落，Play 恢復有效。
6. 再按 Play 後 peak 再次上升。
7. 最後停止、關閉本次建立的 GUI process。

## 結果

- `PASS`：GUI、設定讀回、自動播放、停止、重新播放及 cleanup 全部通過。
- `FAIL`：可重現的產品契約錯誤，例如 Play 按鈕未重新播放。
- `INCONCLUSIVE`：背景音訊使 Endpoint aggregate peak 無法可靠歸因。
- `ERROR`：驗收基礎設施、SVCL 或安全 rollback 失敗。
- `CANCELLED`：在 setter 前取消。

`IAudioMeterInformation` 量測的是 Endpoint 聚合訊號，可證明音訊資料流經該 Endpoint，但不能證明實體喇叭可聽見；實體聲音由使用者確認。

成功套用並精確讀回後，即使播放驗收失敗也會保留選定設定，符合產品語意。若 Apply 結果不明或讀回不符，工具會還原並精確驗證原始設定。
