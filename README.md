# RemoteDesk - 高效能內網原生遠端桌面系統

RemoteDesk 是一套專為 Windows 區域網路（LAN）環境設計的高效能遠端桌面系統，以 .NET 9 (C#) 開發，整合 DirectX 11 DXGI GPU 畫面擷取、低階 Win32 鍵盤/滑鼠輸入注入、微軟輸入法狀態雙向鎖定同步，以及端對端 AES-256-GCM 認證加密通訊，提供流暢、低延遲且具備原生質感的遠端操控體驗。

---

## 核心技術特色

### 1. 原生操控與低延遲輸入
- **硬體掃描碼（Scancodes）對映**：控制端透過底層鍵盤掛鉤（`WH_KEYBOARD_LL`）攔截 Windows 鍵、Alt+Tab、Alt+F4 等系統組合鍵並直接送至被控端，避免本機攔截，實現真實的桌面操控手感。
- **輸入回饋迴圈防護（Echo Prevention）**：注入輸入事件時附加專屬旗標（`Protocol.InjectedMarker`），防止回饋觸發。
- **硬體自適應游標（Hardware Cursor Tracker）**：自動擷取被控端游標圖示（含彩色、單色及 XOR 反相遮罩），於控制端畫布即時渲染，大幅降低游標跟隨延遲。
- **連線防卡鍵保護機制**：於視窗失焦或連線中斷時自動發布按鍵釋放廣播，防止遠端按鍵持續受壓。

### 2. 輸入法雙向鎖定同步（IME Lockstep Synchronization）
- **雙向鎖定模式（Bound Mode）**：
  - 控制端與被控端之輸入法狀態（開啟/關閉、中英轉換狀態、輸入法鍵盤配置語系）完全鎖定連動。
  - 於控制端按下 Shift 或切換輸入法時，被控端立即同步切換；在被控端切換視窗時，控制端亦自動同步中/英狀態，解決遠端桌面常見之中英文不對稱問題。
  - 深度相容 Windows 10/11 微軟新注音、倉頡、拼音及 IMM32 / TSF 架構輸入法。
- **本機組字模式（Local Composition Mode）**：
  - 適用於高延遲或特定網路環境，由本機端負責組字與候選字，遠端回傳插入點游標座標（Caret Position）定位候選視窗，確認後以 Unicode 傳送。

### 3. 多螢幕偵測與 GPU 直出擷取
- **DirectX 11 DXGI 桌面複製**：優先採用 GPU 顯存直出擷取（DXGI Desktop Duplication API），降低 CPU 資源耗損；於 UAC 安全桌面或不支援 DXGI 之環境下自動平滑降級為 GDI BitBlt 擷取。
- **動態多螢幕管理**：支援 Per-Monitor DPI Aware 實體螢幕座標列舉，支援多螢幕熱插拔自動廣播，並提供單一螢幕獨立檢視與全螢幕合併檢視兩種模式。

### 4. 無固定 IP 區域網路動態探索
- **固定 9 位數裝置識別碼**：依據硬體特徵產生專屬 9 位數識別碼（例如 `123 456 789`），便於記憶與辨識。
- **UDP 廣播探索協定**：背景自動廣播並探索區域網路內其他在線的 RemoteDesk 實例，主畫面即時列出主機名稱、IP 與在線狀態，支援雙擊直接發起連線。
- **動態 IP 自動解析**：被控端即便受 DHCP 動態分配 IP，控制端輸入 9 位數 ID 即可在毫秒級時間內完成位址解析並建立連線。

### 5. 端對端安全加密架構
- **ECDH P-256 密鑰交換**：每次連線建立時動態生成橢圓曲線臨時金鑰對，具備前向安全性（Forward Secrecy）。
- **HKDF 密鑰衍生**：分別衍生出控制端至被控端（c2s）與被控端至控制端（s2c）的獨立 AES-256-GCM 加密金鑰。
- **認證加密（AEAD）**：所有傳輸之影像區塊、輸入控制封包及狀態通知均受 AES-256-GCM 認證加密保護，杜絕竊聽與重送竄改。
- **雙軌認證機制**：
  - 無人值守模式：採用 PBKDF2（120,000 次雜湊）與 HMAC 握手挑戰，密碼於本機透過 Windows DPAPI（Data Protection API）安全加密存儲。
  - 互動授權模式：未設定密碼時，遠端連入將彈出授權對話盒，顯示連線端名稱、IP 與 6 位數安全驗證碼，供被控端審核確認。

### 6. 差異影像壓縮傳輸引擎
- **64x64 差異區塊分塊編碼**：僅針對畫面中發生變動的區域進行壓縮與傳輸，大幅減少頻寬需求。
- **自適應影像壓縮**：動態偵測畫面變更頻率，動態區域採用高速 JPEG 壓縮確保操作流暢，靜態細節自動以無損壓縮補傳，維持文字與代碼清晰度。
- **傳輸流量控制（Flow Control ACK）**：依據網路回應時間動態調節傳輸幀率，避免封包積壓引發畫面延遲。

---

## 系統需求

- **作業系統**：Windows 10 / Windows 11 / Windows Server 2019 或更新版本（x64）
- **執行環境**：.NET 9.0 Desktop Runtime（x64）
  - 若使用 `RemoteDeskSetup.exe` 安裝檔，安裝程式會自動偵測並自微軟官方伺服器下載安裝必要的 .NET 9 執行階段。
  - 若使用 `RemoteDesk.msi`，系統需預先安裝 .NET 9 Desktop Runtime。

---

## 下載與安裝

您可以前往本專案的 [Releases 頁面](https://github.com/ItingLai/RemoteDesk/releases) 下載最新安裝套件：

1. **RemoteDeskSetup.exe（推薦）**：
   - WiX Burn 整合安裝開機導引檔（Bootstrapper）。
   - 包含 .NET 9 Desktop Runtime 依賴偵測與線上下載，一鍵完成所有前置準備與軟體安裝。
2. **RemoteDesk.msi**：
   - 標準 Windows Installer 安裝套件。
   - 適合已配置 .NET 9 執行環境之電腦、或企業網域透過 GPO 進行大量派送。

---

## 使用操作指南

### 1. 被控端設定（允許他人連入）

1. 啟動 `RemoteDesk.exe`。
2. 主畫面中央將顯示本機唯一的 **9 位數裝置 ID**（例如：`521 839 204`），將此號碼提供給控制端即可建立連線。
3. 連線授權設定：
   - **無人值守密碼連線**：
     點擊主畫面右上角設定選單中的「安全性設定」，輸入並確認專用連線密碼後儲存。日後控制端輸入此密碼即可直接連入，無須被控端手動點擊同意。
   - **互動確認連線**：
     若未設置無人值守密碼，當有控制端發起連線時，被控端螢幕會彈出「連線授權確認」視窗，顯示對方的裝置名稱、來源 IP 與 6 位數核對碼，點選「允許連線」後即可放行。

### 2. 控制端操作（連線至遠端電腦）

#### 方法 A：透過區域網路自動探索清單連線
1. 啟動 `RemoteDesk.exe`。
2. 主畫面中央區域的「區域網路裝置」卡片會即時列出同一子網內所有正在運行的 RemoteDesk 電腦。
3. 點選目標電腦卡片或直接雙擊，系統即自動發起連線。

#### 方法 B：透過 9 位數裝置 ID 連線
1. 啟動 `RemoteDesk.exe`。
2. 於主畫面輸入框中輸入目標電腦的 9 位數 ID（支援含空格或純數字輸入）。
3. 按下鍵盤 Enter 鍵或點選連線按鈕。
4. 依據目標電腦的設定，輸入連線密碼或等待對方授權同意。

### 3. 遠端作業階段功能

連線成功後將開啟遠端操控視窗，頂部工具列提供以下功能控制：
- **螢幕切換**：若被控端配有多螢幕，可在此自由切換為「螢幕 1」、「螢幕 2」或「所有螢幕合併檢視」。
- **輸入法綁定切換**：可即時切換輸入法「鎖定綁定模式」或「本機模式」，狀態列會顯示當前中/英文連動狀態。
- **全螢幕切換**：按下 `F11` 鍵或工具列按鈕可於全螢幕與視窗模式之間快速切換。
- **傳送系統組合鍵**：工具列選單提供傳送 `Ctrl+Alt+Del`、`Win+L`（鎖定畫面）等系統指令。
- **畫面縮放比例**：支援 100% 原始尺寸點對點顯示與自動配合視窗等比例縮放。

### 4. 進階命令列參數

RemoteDesk 支援透過命令列指令快速啟動或執行特定工作：

```powershell
# 直接連線至指定 9 位數 ID 或 IP
RemoteDesk.exe --connect 123456789
RemoteDesk.exe --connect 192.168.1.100

# 指定自訂設定檔設定（可用於多環境隔離）
RemoteDesk.exe --profile Office --port 21119

# 無主視窗背景服務模式
RemoteDesk.exe --headless
```

---

## 原始碼架構與編譯

### 專案目錄結構

```
RemoteDesk/
├── installer/                       # WiX 安裝程式定義與自動化建置腳本
│   ├── Bundle.wxs                  # WiX Burn Bootstrapper 規格 (含 .NET 9 下載)
│   ├── Package.wxs                 # WiX MSI 安裝套件規格
│   └── build-installer.ps1         # 自動化打包建置 PowerShell 指令檔
├── src/
│   ├── RemoteDesk/                 # RemoteDesk 主程式核心
│   │   ├── Common/                 # 通訊協定、AES-GCM 加密通道、設定檔持久化
│   │   ├── Controls/               # 現代化深色 UI 自訂控制項與向量圖示
│   │   ├── Host/                   # 被控端伺服器、DXGI 畫面擷取、輸入注入、多螢幕
│   │   ├── Native/                 # Win32 P/Invoke 系統底層 API 封裝
│   │   ├── Net/                    # UDP 區域網路廣播與自動探索服務
│   │   ├── Resources/              # 程式圖示與高解析標誌素材
│   │   ├── Viewer/                 # 控制端畫布、輸入法雙向鎖定客戶端、鍵盤掛鉤
│   │   ├── MainForm.cs             # 主視窗控制與流程排程
│   │   └── Program.cs              # 程式進入點與命令列剖析
│   └── RemoteDesk.Installer/       # 備用整合型自解安裝程式專案
├── tests/
│   └── RemoteDesk.Tests/           # 自動化單元與整合測試
│       ├── CryptoAndChannelTests.cs
│       ├── EndToEndLoopbackTests.cs
│       ├── FrameEncoderDecoderTests.cs
│       ├── ImeStateTests.cs
│       ├── LanDiscoveryTests.cs
│       ├── MonitorTests.cs
│       ├── ProtocolTests.cs
│       └── UiComponentTests.cs
├── RemoteDesk.sln                  # Visual Studio 解決方案檔
└── README.md                       # 專案說明與操作手冊
```

### 從原始碼編譯

若欲自原始碼進行編譯與開發，請先確認已安裝 [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)。

```powershell
# 1. 複製專案
git clone https://github.com/ItingLai/RemoteDesk.git
cd RemoteDesk

# 2. 建置解決方案
dotnet build RemoteDesk.sln -c Release

# 3. 執行單元測試
dotnet test

# 4. 發行 Framework-dependent 執行檔
dotnet publish src\RemoteDesk\RemoteDesk.csproj -c Release -r win-x64 --self-contained false -o publish
```

### 建置 WiX 安裝檔

需安裝 WiX Toolset v4 或 v5（可透過 `dotnet tool install --global wix` 安裝）：

```powershell
# 執行自動化建置指令檔，產出位於 dist/ 目錄
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

產出結果：
- `dist/RemoteDesk.msi`
- `dist/RemoteDeskSetup.exe`

---

## 授權條款

本專案採 MIT 授權條款釋出，詳情請參閱專案授權文件。
