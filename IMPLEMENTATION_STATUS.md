# 實作狀況報告 — Audio Device Capability Validator

對照來源：`SPEC.md`（GitHub issue gchang1008/AudioDeviceConfigurator#1 全文）
版本：commit 待提交
自動化測試：170 通過 / 0 失敗
真機驗證：Windows 11 26100 x64、NVIDIA RTX 5070 Ti、ASUS VX229 + ASUS VG27AQL1A（HDMI）、雙螢幕環境

## 總覽

| 標記 | 意義 | 條數 |
|---|---|---|
| ✅ | 完全符合，有測試或真機證據釘住 | 80 |
| ⚠️ | 勉強符合 | 0 |
| ❌ | 不符合 | 0 |

---

## 逐條結果

### 目標選擇與 CLI（1–8）

| # | Story 摘要 | 狀態 | 證據 / 說明 |
|---|---|---|---|
| 1 | 不寫死測試目標 | ✅ | 全程由 EDID 與 Core Audio 動態列舉，程式無任何硬編碼 ID |
| 2 | 自動選預設 Render endpoint | ✅ | `TargetSelector.ResolveEndpoint`；測試 `Selects_the_default_endpoint_when_several_are_active` |
| 3 | 以 ID 選非預設 endpoint 而不改系統預設 | ✅ | 測試 `Reports_the_non_default_endpoint_selected_by_id_without_changing_the_default` |
| 4 | endpoint + monitor ID 參數供無人值守 | ✅ | 測試 `Runs_unattended_when_both_ids_are_supplied`；真機雙螢幕實跑通過 |
| 5 | `--list` 取得可用於腳本的穩定 ID | ✅ | 真機 round-trip 驗證：`--list` 輸出貼回 `--device-id` / `--monitor-id` 可直接跑完整趟 |
| 6 | 歧義配對才互動，不靜默測錯螢幕 | ✅ | `TargetSelector` 以 endpoint 與 monitor 的 `ContainerId` 配對；唯一則自動、多個才互動、零個明確報錯；Windows 哨兵容器永不視為匹配。測試 `PairingByContainerTests`，真機雙螢幕兩台 NVIDIA HDMI 各自動配上自己的螢幕，虛擬喇叭端點正確拒絕靜默配對。 |
| 7 | 可取消歧義選擇 | ✅ | 測試 `Cancels_with_exit_code_three_when_the_user_declines_to_choose` |
| 8 | 取消有獨立 exit code | ✅ | exit 3；測試 `Returns_three_for_user_cancellation` |

### EDID 取得與驗證（9–12）

| # | Story 摘要 | 狀態 | 證據 / 說明 |
|---|---|---|---|
| 9 | 只考慮目前 active 的顯示路徑 | ✅ | `QDC_ONLY_ACTIVE_PATHS`；endpoint 側用 `DEVICE_STATE_ACTIVE` |
| 10 | EDID 取自當前 Windows 環境 | ✅ | 走 CCD API → 該螢幕自身 device key，無歷史紀錄回退 |
| 11 | 每個 block 驗長度與 checksum | ✅ | `EdidParser`；測試涵蓋 base 與各 extension 位置的 checksum 失敗 |
| 12 | 取得或驗證失敗即停止，不假設能力 | ✅ | 拋 `EdidValidationException` → exit 2；測試確認未呼叫任何 SVCL set |

### CTA-861 解析與候選產生（13–28）

| # | Story 摘要 | 狀態 | 證據 / 說明 |
|---|---|---|---|
| 13 | 解析 Audio Data Block | ✅ | `EdidParser.ParseCtaBlock`，含多 ADB、多 CTA block |
| 14 | 解析 LPCM SAD | ✅ | 位元深度／取樣率／最大聲道數 |
| 15 | 非 LPCM 記錄但不測試 | ✅ | JSON 保留 `FormatCode` + `Tested: false`；測試 `Records_non_lpcm_descriptors_as_untested_diagnostics` |
| 16 | 有效 EDID 但無 LPCM → N/A | ✅ | 涵蓋「完全無 ADB」與「只有非 LPCM SAD」兩種 |
| 17 | N/A 為 exit 4 | ✅ | 測試 `Returns_four_for_a_valid_edid_with_no_lpcm` |
| 18 | 重複宣告去重 | ✅ | 測試 `Deduplicates_identical_declarations_while_preserving_every_source` |
| 19 | JSON 保留來源 SAD 參照 | ✅ | `ext1/adb0/sad0` 格式；跨 ADB／跨 block 皆可追溯 |
| 20 | 只測 EDID 宣告的格式 | ✅ | 候選僅由 `LpcmDescriptors` 產生，無推論 |
| 21 | 只有 2/6/8 聲道 | ✅ | `TargetChannelCounts` |
| 22 | 依各 SAD 最大值納入聲道 | ✅ | 測試涵蓋 max 1/2/6/7/8（7 只產生 2 與 6） |
| 23 | 16-bit → 16 容器 | ✅ | `RepresentationFor` |
| 24 | 20-bit → 24 容器 / 20 有效 | ✅ | 同上 |
| 25 | 未宣告則不產生 20-bit | ✅ | 測試 `Does_not_infer_twenty_bit_when_edid_does_not_advertise_it` |
| 26 | 24-bit → 32 容器 / 24 有效 | ✅ | 同上 |
| 27 | 7 種取樣率僅在宣告時納入 | ✅ | 測試涵蓋全 7 種與部分子集 |
| 28 | 聲道→取樣率→位元深度遞增排序 | ✅ | 測試 `Orders_candidates_by_channels_then_rate_then_bit_depth` |

### WASAPI 查詢（29–31）

| # | Story 摘要 | 狀態 | 證據 / 說明 |
|---|---|---|---|
| 29 | 一律 Exclusive 模式查詢 | ✅ | `AUDCLNT_SHAREMODE_EXCLUSIVE`；全程未呼叫 `Initialize`，不建立串流 |
| 30 | 記錄精確 HRESULT | ✅ | 八位十六進位；真機記錄到 `0x88890008` |
| 31 | 非 `S_OK` 不交給 SVCL | ✅ | 測試 `Never_calls_svcl_for_candidates_wasapi_did_not_approve` |

### SVCL 套用與讀回（32–39）

| # | Story 摘要 | 狀態 | 證據 / 說明 |
|---|---|---|---|
| 32 | 每個 WASAPI 支援的候選都套用 | ✅ | 真機 6 個候選全數套用並讀回 |
| 33 | 從 raw format 結構讀回，不依賴在地化文字 | ✅ | 直接解析 `/SaveDeviceFormat` 位元組 |
| 34 | EXTENSIBLE 分開讀有效／容器位元 | ✅ | 測試區分「32 容器 + 24 有效」與「真 32-bit」 |
| 35 | 一般 WAVEFORMATEX 以容器位元為有效位元 | ✅ | 測試 `Reads_effective_bit_depth_from_container_bits_of_a_plain_waveformatex` |
| 36 | 每 200 ms 輪詢、上限 3 秒 | ✅ | 測試驗證延遲讀回可通過、逾時則失敗、輪詢次數落在預算內 |
| 37 | 三個值必須完全相符 | ✅ | 聲道／取樣率／位元深度各有替換測試 |
| 38 | 保留 stdout / stderr / exit code | ✅ | `SvclCommands` 陣列，含失敗原因 |
| 39 | `No items found` 即使 exit 0 也算失敗 | ✅ | 測試 `Treats_no_items_found_with_exit_code_zero_as_a_failure` |

### 狀態還原（40–47）

| # | Story 摘要 | 狀態 | 證據 / 說明 |
|---|---|---|---|
| 40 | 第一次修改前先存原始格式 | ✅ | 測試斷言第一個 SVCL 呼叫必為 `/SaveDeviceFormat` |
| 41 | 失敗候選後立即還原並驗證再繼續 | ✅ | 測試斷言呼叫序列：失敗套用 → 還原 → 下一候選 |
| 42 | 成功候選可直接進入下一個 | ✅ | 測試斷言三個成功候選之間無還原、結尾僅一次 |
| 43 | 正常結束時還原並驗證 | ✅ | 真機驗證還原回 `2 ch, 16 bit, 48000 Hz` |
| 44 | 例外後仍嘗試還原 | ✅ | 測試 `Attempts_restoration_after_a_handled_exception` |
| 45 | Ctrl+C 攔截並還原 | ✅ | 測試以 `CancellationToken` 驅動；`Program` 設 `e.Cancel = true` |
| 46 | 還原失敗即停止後續測試 | ✅ | 剩餘候選標記 `NotTested` |
| 47 | 還原失敗回報 exit 2 | ✅ | **曾有缺陷，已修**（見下方「已修正缺陷」） |

### 不得為之的約束（48–53）

| # | Story 摘要 | 狀態 | 證據 / 說明 |
|---|---|---|---|
| 48 | 不改系統預設播放裝置 | ✅ | 全程只用 `/SetDefaultFormat` 與 `/SaveDeviceFormat`，無 `/SetDefault`、`/Switch` |
| 49 | 不呼叫 `/SetSpeakersConfig` | ✅ | 測試明確斷言不存在該呼叫 |
| 50 | 只改預設格式的聲道數 | ✅ | 聲道數只透過預設格式變更，未觸及喇叭組態 |
| 51 | 不關閉其他應用程式 | ✅ | 唯一的 `Kill` 是 SVCL 逾時殺自己的子行程，不涉及他人 |
| 52 | 誠實回報競用／修改失敗 | ✅ | 讀回不符即記為失敗，不重試掩蓋 |
| 53 | 不強制系統管理員權限 | ✅ | 無 manifest 提權；真機以標準帳戶跑通 |

### 部署（54–58）

| # | Story 摘要 | 狀態 | 證據 / 說明 |
|---|---|---|---|
| 54 | .NET 10 self-contained 單檔 win-x64 | ✅ | 產出單一 73 MB exe，無需安裝 runtime |
| 55 | 啟動檢查 SVCL ≥ 1.28 | ✅ | 讀 `FileVersionInfo.ProductVersion`，NirSoft 在資源裡直接寫 marketing 字串 `1.28`，不是 `(1,2,8,0)` 的四位檔案版號。測試涵蓋 `1.28.0.0` / `1.30.0.0` / `2.0.0.0` 通過與 `1.27.0.0` / `1.20.0.0` 拒絕；真機報告欄位 `SvclVersion = "1.28"`。 |
| 56 | 缺少或過舊的 SVCL 歸為系統錯誤 | ✅ | 兩種情況皆 exit 2 並有明確訊息 |
| 57 | 保留未修改的 SVCL exe / readme / CHM | ✅ | `svcl-x64/` 三檔原封不動，發佈時一併複製 |
| 58 | 無第三方 NuGet 依賴 | ✅ | 主專案零 `PackageReference`。PowerShell 腳本作為內嵌資源以抓 GPU/HDMI audio driver metadata（故事 67），PowerShell 是 Windows 內建不算第三方依賴。 |

### 主控台輸出（59–62）

| # | Story 摘要 | 狀態 | 證據 / 說明 |
|---|---|---|---|
| 59 | 全英文文字與欄位名 | ✅ | 已設 `Console.OutputEncoding = UTF8`，在地化裝置名可正確顯示 |
| 60 | 聲道以數字呈現 | ✅ | 測試斷言不出現「5.1」「7.1」「Surround」等字樣 |
| 61 | 通過格式的精簡表格 | ✅ | 真機輸出四欄表格 |
| 62 | 摘要 unsupported / failed 計數 | ✅ | 真機輸出 `Passed: 6 / Unsupported: 3 / Apply failed: 0` |

### 報告（63–73）

| # | Story 摘要 | 狀態 | 證據 / 說明 |
|---|---|---|---|
| 63 | 所有候選（含失敗）都保留 | ✅ | 測試斷言三種狀態並存 |
| 64 | CSV 每格式一列 | ✅ | 測試斷言 header + 6 列 |
| 65 | 每列重複系統／螢幕／端點／驅動資訊 | ✅ | CSV 每列重複 system/endpoint/monitor 與 driver metadata；測試 `Repeats_system_monitor_endpoint_and_driver_metadata_in_every_csv_row`；真機 6 列都有完整 driver 欄位 |
| 66 | JSON 含 raw EDID 位元組 | ✅ | 完整 hex，與輸入位元組逐一相符 |
| 67 | **含 PC 名、Windows 版本、GPU 與音訊驅動細節等** | ✅ | GPU driver 經 PowerShell 內嵌腳本抓 `Win32_PnPSignedDriver`（DeviceClass=Display）；HDMI audio driver 同腳本抓 DeviceClass=MEDIA。JSON `Monitor.GpuDriverVersion=32.0.16.1047`、`Monitor.GpuDriverName=NVIDIA GeForce RTX 5070 Ti`、`Endpoint.DriverVersion=1.4.5.7`，真機驗證於 2026-07-29。 |
| 68 | 通過／失敗／錯誤／N/A／取消都產生報告 | ✅ | 各情境皆有測試 |
| 69 | 早期失敗或 N/A 的 CSV 含摘要列 | ✅ | 測試斷言 header + 1 摘要列 |
| 70 | 報告置於執行檔旁的 `Reports` | ✅ | 真機產出於 `publish\Reports\` |
| 71 | 檔名含淨化後 PC 名、螢幕名、本地時間 | ✅ | `GCHANG1008_VX229_20260728_184744` |
| 72 | 同秒衝突加序號後綴 | ✅ | 測試斷言 `_2.json` / `_2.csv` |
| 73 | 時間戳為本地時間 | ✅ | 含時區偏移 `+08:00` |

### Exit code（74–80）

| # | Story 摘要 | 狀態 | 證據 / 說明 |
|---|---|---|---|
| 74 | 0 = 完全通過 | ✅ | 測試 |
| 75 | 1 = 格式不符 | ✅ | 真機實測 exit 1 |
| 76 | 2 = EDID／配對／SVCL／還原／報告等系統錯誤 | ✅ | 各來源皆有測試 |
| 77 | 3 = 使用者取消 | ✅ | 測試 |
| 78 | 4 = 有效螢幕但無 LPCM | ✅ | 測試 |
| 79 | 報告寫入失敗歸為系統錯誤 | ✅ | 目錄建立、JSON、CSV 三種失敗皆測試 |
| 80 | `--help` 說明命令、參數、報告、副作用、exit code | ✅ | 測試逐項斷言五個 exit code 說明皆在 |

---

## ❌ 不符合項目

（無）

---

## ⚠️ 勉強符合項目

（無）

---

## 已修正缺陷

### Story 67 — 透過 PowerShell 內嵌腳本抓 GPU/HDMI audio driver metadata

之前在 .NET 程式內嘗試過三條路徑抓 GPU driver version，全部失敗（見 git log commit `00dbd27`）：

1. Registry `HKLM\...\Class\{Display}` 比對 `DriverDesc` —— `EnumDisplayDevices` 對 monitor 路徑返回 monitor 名而非 adapter 名，比對不到。
2. SetupAPI `SetupDiOpenDeviceInfoW` 走 monitor PnP 父鏈 —— 無 class 綁定的 list 不收 monitor devnode（`cr=1 INVALID_FUNCTION`）；`CM_Get_DevNode_PropertyW(DEVPKEY_Device_ClassGuid/Parent)` 對 monitor devnode 回 `CR_NO_SUCH_VALUE`（monitor 是 PnP 樹根）。
3. WMI `Win32_VideoController` —— PowerShell 端驗證能直接拿到 `32.0.16.1047`，但 .NET 端要嘛用 `System.Management` NuGet（違反故事 58），要嘛手寫 WMI COM 介面（500+ 行），時間預算不允許。

**轉折**：在 .NET 程式內抓取，與從主程式 spawn 外部程式抓取，是兩種不同的設計。PowerShell 內建於 Windows 10/11，**不算第三方依賴**（NuGet 才是第三方），把 `Get-CimInstance Win32_PnPSignedDriver` 包進 PowerShell 腳本作為 `<EmbeddedResource>` 內嵌進 .NET assembly，就能同時滿足：

- 故事 67：GPU 與 HDMI audio driver version 都有值
- 故事 58：主專案零 `PackageReference`
- 規格的「differences across PCs」：報告含 `Monitor.GpuDriverVersion=32.0.16.1047` 與 `Endpoint.DriverVersion=1.4.5.7`

實作：`scripts/Get-DriverInfo.ps1` 內嵌成 `AudioDeviceConfigurator.scripts.Get-DriverInfo.ps1` 資源；`PowerShellDriverMetadataProvider` 在執行時把腳本寫到 temp `.ps1` 檔、spawn `powershell.exe -File` 跑它、解析 stdout JSON、刪 temp 檔。`CoreAudioEndpointProvider` 以括號內 driver name 對 `AudioHdmiDriver.Name` 配對填回 `Endpoint.DriverVersion`；`WindowsDisplayProvider` 從 Gpu 物件直接填回 `Monitor.AdapterName`/`GpuDriverName`/`GpuDriverVersion`/`GpuDriverProvider`。

真機驗證（2026-07-29）：`Monitor.GpuDriverVersion=32.0.16.1047`、`Endpoint.DriverVersion=1.4.5.7`、`Monitor.AdapterName=NVIDIA GeForce RTX 5070 Ti`。


### Story 55 — 改讀 `ProductVersion` 而非 `FileVersion`（commit 待提交）

之前 `SystemFileSystem.GetFileVersion` 用 `FileVersionInfo.FileMajorPart` 等結構化欄位組字串，回傳 `1.2.8.0`。為了把它當作 `1.28` 比對，加了 `NormalizeNirsoftVersion` 把第二、三位拼接。**這是把檔案版本冒充成 marketing 版本**，我承認是猜測。

真相在 NirSoft 的 VERSION_INFO 資源：`ProductVersion` 字串欄位本身就是 `"1.28"`，不是 `(1,2,8,0)`。改用 `info.ProductVersion` 後直接拿 `"1.28"`，不需要任何 `NormalizeNirsoftVersion`，也不需猜版號慣例。`FileVersionInfo` 在 .NET 上對字串欄位的支援穩定 —— 沒有資源時回 `""`，不會誤讀。

真機驗證：報告 `SvclVersion = "1.28"`，與規格要求的 marketing 版本字串一致。

### Story 6 — 端點與螢幕依 device container 配對（commit `b36d2fc`）

原本只看 active display 數量：1 個就直接用、>1 個就問人，**完全沒用 endpoint 的資訊**。這代表單螢幕環境下，USB 喇叭或虛擬喇叭端點會被靜默配上不屬於它的螢幕，正是 story 6 要防的事。

HDMI/DP 音訊端點與其螢幕共用同一個 device container GUID。改用它配對後：唯一則自動、多個才互動、零個明確報錯；Windows「無容器」哨兵值永不視為匹配。

端點容器以 `VT_CLSID` 而非字串形態送達，需透過新增的 `PropVariant.AsGuid` 解讀，否則一律回傳 null。螢幕容器走 `CM_Get_DevNode_PropertyW` 從 PnP 樹直接讀。

真機驗證（雙螢幕、雙 NVIDIA HDMI）：兩端點各自動配上自己的螢幕，零提示；Steam 虛擬喇叭端點（哨兵容器）正確拒絕靜默配對，改為詢問。

### Story 47 / 79 — 還原失敗被重試而掩蓋（commit `5e03941`）

原本 per-candidate 還原失敗後，`Run()` 的外層還原區塊會再試一次。若重試成功，`Restore.Succeeded` 被覆寫為 `true`，於是 JSON/CSV 聲稱還原乾淨、exit code 卻是 2 —— 正是 story 79「缺少證據不得被誤認為有效完成」要防的錯配。

已加旗標讓失敗過的還原永不重試，並補回歸測試 `Never_reports_a_failed_restore_as_succeeded_after_a_later_retry`。

---

## 規格外的額外改動

### `--monitor-id` 容許省略 `\\?\` 前綴（commit `7a1aebd`）

`--list` 印出的螢幕 ID 帶 Win32 命名空間前綴，在 cmd、PowerShell、bash 三者中各自被不同規則破壞，貼回去必定失敗，削弱了 story 5 的腳本化用途。已讓比對時兩側都忽略該前綴。

此前綴是所有 active display path 的常數，不屬螢幕身分，去除後仍為單射 —— 兩台不同螢幕不會塌縮成同一筆。已有測試確認「僅在前綴之後有差異的兩台」仍可區分，且未知 ID 仍被拒絕。

**這是規格未要求的可用性改善，非缺陷修復。**

---

## 建議處理順序

（無未完成項目）

---

## 故事編號驗證

80 條故事編號完整唯一（1..80 無缺漏、無重複），以 `awk` 從 IMPLEMENTATION_STATUS.md 掃描逐條驗證。
