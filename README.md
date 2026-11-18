# RemoteDesk (v2.0.1)

專為 Windows 10/11 環境設計的原生跨網路遠端桌面控制系統（.NET 9 + WinForms）。從 v1.x 的純區域網路架構全面演進為 **「LAN TCP 直連 + WAN 純 P2P IPv4 UDP 穿透」** 雙模傳輸架構，在保有極致低延遲、端對端 AES-256-GCM 加密與原生日文／中文輸入法同步的同時，進一步支援多網卡／雙 WAN、NAT 穿透打洞、可靠 UDP（ARQ）、連線中無縫路徑切換與自適應畫質串流。

---

## v2.0.1 修正摘要
* **無人值守總開關與密碼登入統一管控**：關閉「啟用無人值守存取模式」後，會同步停用可信裝置自動接受、雙軌並行密碼與固定連線密碼直接登入，所有連入請求皆須經被控端現場核准（設定介面亦同步即時顯示固定密碼因未啟用無人值守而暫停使用之狀態提示）。
* **修正清除密碼後的過期快取迴圈**：被控端清除密碼或停用無人值守密碼後，控制端連線時依 `ServerHello.HasPassword` 自動略過並清除本機過期快取密碼，改走正常授權確認流程；被控端亦對舊版用戶端之殘留密碼請求安全降級為「是否接受」互動彈窗，不再出現控制端無限提示密碼、被控端未彈窗之問題。
* **雙軌並行授權對話框競態修正**：補強控制端密碼等待視窗與被控端快速核准之間的同步保護，避免視窗關閉競態。

## v2.0.0 核心特色

### 1. WAN 純 P2P IPv4 UDP 穿透架構
* **去中心化影像直連**：所有螢幕串流與鍵鼠控制皆直接於受控端（Host）與控制端（Viewer）之間點對點傳輸，中央 Signaling 服務僅協助 9 位數裝置 ID 查詢與 P2P 握手協調，完全不中繼桌面影像或輸入封包。
* **多網卡／雙 WAN 與出口路由綁定**：自動偵測本機可用 IPv4 實體／虛擬網路介面，為每張介面建立獨立 UDP Socket 並透過 Windows `IP_UNICAST_IF` 鎖定實體出口網卡，同時提供 **VPN Candidate Policy**（自動／納入／排除）設定。
* **多 STUN 探測與 Candidate 收集**：支援對多組公開 STUN 伺服器進行並發探測，收集 `Host`、`Srflx` 與 `prflx` 候選端點並分析 NAT 映射行為。
* **ICE 風格雙向連通性檢查與 Nomination**：依據候選優先權配對並執行雙向連通性驗證（Connectivity Check），由 `Controlling` 端統一執行 Nomination 選定最佳直連路徑，避免雙端各自切換衝突。
* **智慧失敗分類與多輪策略性自動重試**：透過 `P2PRetryCoordinator` 依失敗原因自動切換策略重試（原有配對快驗 -> 輪替 STUN 與重整介面／Socket 重收集 -> 放寬逾時匯聚協商）。

### 2. Reliable UDP、路徑監控與斷線無縫修復
* **Reliable UDP ARQ 串流層 (`ReliableUdpStream`)**：完全實作標準 `System.IO.Stream`，提供 `< 1200 bytes` MTU 安全切片、256 封包滑動視窗累積 ACK、Fast Retransmit 快速重傳、RFC 6298 動態 SRTT/RTO 估算、Token Bucket 平滑發送節流（Pacing）以及 Keepalive 心跳保活，可無縫承載 ECDH + AES-256-GCM 加密通道。
* **即時路徑品質監控與無縫切換 (`P2PPathMonitor` + `PathEpoch`)**：持續追蹤雙向心跳 RTT、滑動視窗封包遺失率與重傳率；當偵測介面斷線、NAT 映射漂移或路徑劣化時，透過 `PathEpoch` 世代防護原子切換至備援介面／路徑，維持現有工作階段不中斷。
* **FrameSeq ACK 世代安全與自適應解析度**：導入 `FrameSeq ACK` 精準回報影格確認與往返延遲，結合 `AdaptiveResolutionController` 在「自動 / 原生鎖定 / 流暢優先」三種模式間（100% / 75% / 50%）依實際壅塞與丟包狀況平順升降解析度並動態調整 JPEG 畫質。

### 3. 連線可靠度與多實例防護
* **DNS Sinkhole 防護與 DoH 解析 (`SignalingClient`)**：支援系統 DNS 與 Cloudflare DoH (`1.1.1.1` / `1.0.0.1`) 雙路徑解析 Signaling 網域，全程強制 HTTPS / WSS TLS 憑證驗證。
* **單實例守護與在線狀態追蹤**：`SingleInstanceGuard` 透過具名 Windows Mutex 與啟用事件避免同 Profile 重複啟動，並允許透過 `--profile <名稱>` 多組設定獨立並存；`DeviceOnlineStatusTracker` 整合主動工作階段、LAN 廣播與遠端查詢即時顯示收藏與最近連線裝置在線狀態。

---

## 核心功能與架構

### 雙軌傳輸與畫面串流
* **DXGI Desktop Duplication 硬體加速擷取**：透過 `Vortice.Direct3D11` 與 `Vortice.DXGI` 直接由 GPU 擷取桌面畫面，低 CPU 佔用，並針對無頭環境與多螢幕具備自動切換與錯誤降級機制（GDI 備援與動態重設）。
* **LAN 直連 + WAN P2P 自動協作**：
  * **LAN 直連**：預設使用 TCP Port `21119` 直連，並透過 UDP Port `21118` 進行區域網路廣播自動探索。
  * **WAN P2P**：輸入 9 位數裝置 ID 即可透過 Signaling 協調雙端 UDP 打洞建立點對點加密隧道。
* **鼠標與多螢幕無縫體驗**：支援遠端即時游標追蹤與視覺補償、多螢幕獨立切換檢視，以及自適應解析度與 JPEG 畫質調整。

### 安全與權限控制
* **端對端加密通道 (`SecureChannel`)**：
  * 使用 **ECDH P-256** 執行暫態金鑰交換，並以 **HKDF** 衍生雙向獨立會話金鑰。
  * 採用 **AES-256-GCM**（12-byte Nonce + 16-byte Auth Tag）為所有影像影格與鍵鼠指令進行認證加密，並以漸進計數器防範重放攻擊。
* **多重授權模式**：
  * **手動確認授權**：受控端彈出連線核准視窗，顯示請求方裝置 ID、位址及 6 位數安全驗證碼，可選擇「僅接受本次」或「接受並信任此裝置」。
  * **無人值守永久密碼**：支援以隨機鹽值（Salt）結合 **PBKDF2-SHA256** 進行密碼驗證，透過挑戰-回應（Challenge-Response）機制於不傳輸明文密碼的前提下完成鑑權，授權資訊經 Windows **DPAPI (`ProtectedData`)** 安全加密儲存。

### 輸入、剪切板與 IME 同步
* **輸入模擬 (`InputInjector`)**：完整的鍵鼠事件與高解析度滾輪轉譯，支持複合組合鍵（如 Ctrl+C、Alt+Tab、Win+D 等），切換視窗或斷線時自動釋放按鍵狀態以防卡鍵。
* **雙向剪切板同步**：控制端與受控端之間即時雙向同步文字剪貼簿，並以內容雜湊防護避免迴圈複製。
* **遠端 IME 狀態同步 (`ImeController` / `ImeSyncClient`)**：支援全螢幕與視窗化操控下的輸入法中英狀態與複合輸入行為協調。

---

## 系統需求與環境

* **作業系統**：Windows 10 / Windows 11 (x64)
* **開發框架**：.NET 9 (`net9.0-windows`)
* **依賴套件**：
  * `Vortice.Direct3D11` (v3.8.3)
  * `Vortice.DXGI` (v3.8.3)
  * `System.Security.Cryptography.ProtectedData` (v9.0.0)
  * `System.ServiceProcess.ServiceController` (v9.0.0)
* **打包工具**：WiX Toolset v4+（含 `WixToolset.UI.wixext` 與 `WixToolset.Util.wixext`，建置 MSI 與 Burn Bootstrapper 時需要）

---

## 建置與執行指南

### 1. 從原始碼編譯與測試
```bash
# 還原並以 Release 模式建置完整方案
dotnet build RemoteDesk.sln -c Release

# 執行單元與回歸測試
dotnet test RemoteDesk.sln -c Release

# 啟動應用程式
dotnet run --project src/RemoteDesk/RemoteDesk.csproj -c Release
```

### 2. 產出發行執行檔與 Windows 安裝檔
執行自動化 WiX Toolset 打包腳本，將自動發布 `RemoteDesk.exe` 並編譯產出 **`dist/RemoteDesk.msi`** 與 **`dist/RemoteDeskSetup.exe`**（內建 .NET 9 Desktop Runtime x64 檢測與線上下載安裝流程）：
```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1
```

### 3. 命令列引數 (CLI Arguments)
應用程式支援透過命令列參數快速連線或建立獨立設定檔：
```text
--connect <ID/IP>    啟動後自動嘗試連線至指定 9 位數裝置 ID 或 IP(:Port)
--profile <名稱>     使用獨立設定檔目錄 (DataDirectory) 與單實例鎖，便於多實例分開配置與測試
--port <連接埠>      指定本機 TCP 監聽連接埠 (預設 21119)
--help, -h           顯示命令列使用說明對話框
```

---

## 專案結構

```text
RemoteDesk/
├── RemoteDesk.sln                        # Visual Studio / .NET 方案檔
├── installer/                            # WiX Toolset v4 安裝包建置腳本與定義
│   ├── Bundle.wxs                        # Burn Bootstrapper (.NET 9 Desktop Runtime 檢測/安裝)
│   ├── Package.wxs                       # MSI 安裝包元件與捷徑定義
│   └── build-installer.ps1               # Release 發布與 MSI / Setup.exe 自動化建置腳本
├── src/
│   ├── RemoteDesk/                       # 主程式 (.NET 9 WinForms, x64)
│   │   ├── Common/                       # 設定 (AppConfig)、協定、加密通道 (SecureChannel)、裝置識別、自適應解析度規則與單實例守護
│   │   ├── Controls/                     # 深色主題自繪卡片與向量圖示元件
│   │   ├── Host/                         # 受控端邏輯 (DXGI 擷取、FrameEncoder、自適應解析度、輸入模擬、存取核准)
│   │   ├── Native/                       # Win32 P/Invoke 宣告 (Input, IMC/IME, DWM/DXGI)
│   │   ├── Net/                          # LAN 探索、SignalingClient (DoH)、Multi-NIC/STUN、ICE 打洞、ReliableUdpStream、P2PPathMonitor
│   │   ├── Viewer/                       # 控制端視窗、桌面畫布、FrameDecoder、KeyboardHook、IME 同步
│   │   ├── MainForm.cs                   # 主控面板 (裝置 ID、遠端連線、收藏／最近設備在線狀態與系統匣)
│   │   ├── ConnectionHistoryForm.cs      # 連線紀錄檢視視窗
│   │   ├── SettingsForm.cs               # 本機設定 (無人值守密碼、畫質／解析度模式、VPN 候選策略)
│   │   └── Program.cs                    # 程式進入點與 CLI 參數解析
│   └── RemoteDesk.Installer/             # 獨立圖形化安裝程式引擎專案
└── tests/
    └── RemoteDesk.Tests/                 # xUnit 測試專案 (涵蓋協定、加解密、Multi-NIC、STUN/ICE、ReliableUDP 與編解碼回歸測試)
```

---

## 安裝包與執行檔說明

* **`RemoteDeskSetup.exe`**（推薦）：WiX Burn 安裝引導程式，會自動檢測目標電腦是否已安裝 Microsoft .NET 9 Desktop Runtime (x64)，若尚未安裝將自動下載官方執行環境並靜默安裝，隨後完成 RemoteDesk 主程式部署。
* **`RemoteDesk.msi`**：純 Windows Installer 安裝套件，適用於已具備 .NET 9 Desktop Runtime (x64) 之環境直接安裝或企業批次佈署。

---

## 疑難排解

* **跨外網 P2P 連線失敗或耗時過長**：
  * 若雙端均位於嚴格對稱型 NAT（Symmetric NAT）或企業級 UDP 封鎖防火牆環境，純 P2P UDP 打洞可能受限，建議確認路由器啟用 UDP NAT 穿透或放行出站 UDP 流量。
  * 若環境啟用多個 VPN 虛擬介面影響候選配對，可於「設定」中調整 **VPN 候選介面策略**（自動 / 排除）。
* **畫面擷取異常或全黑**：
  * 當處於多 GPU 環境或 UAC 權限提示畫面切換時，DXGI 可能觸發暫時性裝置中斷；程式內建自動切換與 GDI 備援擷取機制可維持連線。如遇完全黑屏，請確認未於受控端鎖定螢幕且遠端服務以適當權限運行。
* **區域網路無法發現裝置**：
  * 確認雙端皆連入同一區域網路子網，且 Windows 防火牆允許 TCP Port `21119`（資料傳輸）及 UDP Port `21118`（區域廣播探索）。
