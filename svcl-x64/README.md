# Test-DefaultAudioFormat.ps1

驗證 Windows 預設音訊裝置的格式是否可被 `svcl.exe` 設定為指定的值，並回讀確認實際生效。

## 需求

- Windows 11（Windows 10 SDK SVCL 1.2）
- PowerShell 5.1 以上
- 與腳本同目錄下需有 `svcl.exe`

## 使用方式

```powershell
.\Test-DefaultAudioFormat.ps1 [BitsPerSample] [SampleRate] [Channels] [Device]
```

## 參數

| 參數 | 預設值 | 範圍 | 說明 |
|---|---|---|---|
| `-BitsPerSample` | 16 | 1–64 | 每取樣位元數 |
| `-SampleRate` | 48000 | 1–768000 | 取樣率（Hz） |
| `-Channels` | 2 | 1–32 | 聲道數 |
| `-Device` | `DefaultRenderDevice` | — | 目標裝置 ID |

## 執行流程

1. 呼叫 `svcl.exe /SaveDeviceFormat` 讀取設定前的預設格式
2. 若目標為 2／4／6／8 聲道，先以對應 mask 呼叫 `svcl.exe /SetSpeakersConfig`
3. 呼叫 `svcl.exe /SetDefaultFormat` 套用指定格式
4. 等待 500 毫秒後再讀取一次
5. 比對前後值是否一致，輸出 PASS 或 FAIL

### 對應的 svcl.exe 指令

| 步驟 | 動作 | 對應指令 |
|---|---|---|
| 1 | 讀取設定前的格式 | `svcl.exe /SaveDeviceFormat $Device $tempFormatPath` |
| 2 | 套用指定格式 | `svcl.exe /SetDefaultFormat $Device $BitsPerSample $SampleRate $Channels` |
| 3 | 等待 500ms 後再讀取 | `svcl.exe /SaveDeviceFormat $Device $tempFormatPath` |

預設參數下會執行的指令範例：

```
svcl.exe /SaveDeviceFormat  DefaultRenderDevice <tempFile>
svcl.exe /SetDefaultFormat  DefaultRenderDevice 16 48000 2
svcl.exe /SaveDeviceFormat  DefaultRenderDevice <tempFile>
```

### 喇叭配置

若既有喇叭配置與目標聲道數不一致，先執行 `/SetSpeakersConfig`，再執行
`/SetDefaultFormat`。各聲道數的配置如下：

| 聲道數 | 喇叭配置 | 指令 |
|---:|---|---|
| 2 | Stereo | `svcl.exe /SetSpeakersConfig DefaultRenderDevice 0x3 0x3 0x3` |
| 4 | Quadraphonic | `svcl.exe /SetSpeakersConfig DefaultRenderDevice 0x33 0x33 0x33` |
| 6 | 5.1 Surround | `svcl.exe /SetSpeakersConfig DefaultRenderDevice 0x3f 0x3f 0x3f` |
| 8 | 7.1 Surround | `svcl.exe /SetSpeakersConfig DefaultRenderDevice 0x63f 0x63f 0x63f` |

例如切換為 4 channel：

```powershell
svcl.exe /SetSpeakersConfig DefaultRenderDevice 0x33 0x33 0x33
svcl.exe /SetDefaultFormat DefaultRenderDevice 24 48000 4
```

> 注意：腳本只切換到指定格式，不會在結束時還原原本的 `Before` 設定。如需還原，請手動呼叫 `svcl.exe /SetDefaultFormat` 把 `Before` 的值寫回。

## 退出碼

| 退出碼 | 意義 |
|---|---|
| `0` | 設定成功且讀回值與請求一致 |
| `1` | 設定後讀回值與請求不符 |
| `2` | 找不到 `svcl.exe` 或 SVCL 執行失敗 |

## 範例

```powershell
# 使用預設參數：16bit / 48kHz / 2ch，目標 DefaultRenderDevice
.\Test-DefaultAudioFormat.ps1

# 改為 24bit / 96kHz / 立體聲
.\Test-DefaultAudioFormat.ps1 24 96000 2

# 測試指定的播放裝置
.\Test-DefaultAudioFormat.ps1 -Device 'CommunicationsRenderDevice'
```

## 輸出範例

```
Device: DefaultRenderDevice
Before: 2 channels, 16 bit, 48000 Hz
Requested: 2 channels, 24 bit, 96000 Hz
After: 2 channels, 24 bit, 96000 Hz
Result: PASS
```

## 注意事項

- 腳本會將暫存檔寫入系統 Temp 目錄（`svcl-format-<GUID>.dat`），結束時自動清理
- 對於 `WAVEFORMATEXTENSIBLE`（`wFormatTag == 0xFFFE`）格式，會以延伸區段中的 `wValidBitsPerSample` 作為位元數回報
- 設定生效後請給予裝置約 500 毫秒穩定時間再回讀
