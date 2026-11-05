# RemoteDesk - 內網原生遠端桌面系統

一個專為**區域網路（內網）環境**打造的高效能遠端桌面系統，設計哲學與體驗全面對標 AnyDesk，提供極致流暢、低延遲的操控感受。

---

## 核心特色與技術架構

### 1. 原生操控體驗 (Native Control)
- **實體鍵盤掃描碼 (Scancodes) 映射**：透過底層鍵盤 Hook (`WH_KEYBOARD_LL`) 攔截 Windows 鍵、Alt+Tab、Alt+F4 等系統組合鍵並直接送往遠端，避免本機攔截，實現如同坐在電腦前的原生操控。
- **防止回饋迴圈 (Echo Prevention)**：注入輸入時打上專屬標記 (`Protocol.InjectedMarker`)，杜絕連線自身時產生無窮回授。
- **自適應游標形狀 (Hardware Cursor Tracker)**：自動擷取遠端游標圖示（彩色/單色/XOR 反相），控制端直接以本機滑鼠游標繪製，操作完全零延遲。
- **防卡鍵保護機制 (Release All)**：視窗切換或連線中斷時，自動釋放所有按壓中的鍵盤與滑鼠鍵。

### 2. 輸入法雙向鎖定綁定 (IME Lockstep Synchronization)
> **解決傳統遠端桌面最令人困擾的「一邊中文、一邊英文」問題！**
- **雙向同步模式 (`ImeSyncMode.Bound`)**：
  - 本機與遠端輸入法狀態（開啟/關閉、中/英文轉換模式、鍵盤配置語系）完全鎖定連動。
  - 在本機按下 Shift 或切換輸入法，遠端立即同步切換為中文或英文；在遠端切換視窗時，本機也會自動切換至相應的中/英狀態。
  - 相容 Windows 10/11 微軟新注音、倉頡、拼音以及各種 IMM32 / TSF 輸入法。
- **本機組字模式 (`ImeSyncMode.Local`)**：
  - 適合極高延遲網路；本機負責組字與候選字，遠端回傳插入點游標座標（Caret Position）精準定位候選視窗，確認後的字詞以 Unicode 完整送出。

### 3. 多螢幕自動偵測與即時切換 (Multi-Monitor Detection & Switching)
- **硬體螢幕偵測**：支援 Per-Monitor DPI Aware 列舉實體螢幕解析度、座標與主螢幕標記。
- **動態熱插拔適應**：遠端若有插拔螢幕或調整解析度，自動廣播新的螢幕清單給控制端。
- **切換檢視模式**：
  - 單一螢幕獨立顯示（螢幕 1、螢幕 2...）。
  - 所有螢幕合併檢視（支援跨螢幕全桌面控制）。
- **GPU 直出擷取 (DXGI Desktop Duplication)**：
  - 優先採用 DirectX 11 DXGI 桌面複製 API，直接從 GPU 顯存擷取畫面，極低 CPU 消耗。
  - 在安全桌面（UAC / 鎖定畫面）或特殊情況下自動無縫降級為 GDI BitBlt 擷取。

### 4. 無固定 IP 區域網路動態探索 (Dynamic LAN Discovery)
- **穩定 9 位數裝置 ID**：根據機器硬體特徵產生穩定的 9 位數 ID（例如 `123 456 789`），重灌設定檔或重新啟動也不會變更。
- **UDP 廣播探索協定**：
  - 背景定期宣告並偵測區網內的主機。
  - **自動探索清單**：主畫面即時列出同一區域網路內所有可連線的電腦、登入使用者與主機名稱，點擊兩下即可連線。
  - **動態 IP 自動解析**：即使遠端電腦使用 DHCP 動態分配 IP，只要輸入 9 位數 ID 即可在毫秒內尋獲並建立連線。

### 5. 軍規級安全通訊協定 (End-to-End Encryption)
- **ECDH P-256 金鑰交換**：每次連線動態產生橢圓曲線金鑰對，具備前向安全性 (Forward Secrecy)。
- **HKDF 衍生密鑰**：分別產生控制端至主機 (c2s) 與主機至控制端 (s2c) 的專屬 AES 金鑰。
- **AES-256-GCM 傳輸加密**：所有影像區塊、按鍵事件與剪貼簿資料皆受 AES-GCM 認證加密保護，防止竊聽與篡改。
- **雙重驗證方式**：
  - **無人值守固定密碼**：PBKDF2 120,000 次雜湊與 HMAC 握手證明。密碼於本機端使用 Windows DPAPI 安全加密儲存。
  - **互動確認模式**：若未設密碼，遠端連入時主機會跳出確認視窗，顯示連線者名稱、IP 與 6 位數安全驗證碼（兩端核對無誤後按「接受」即可連線）。

### 6. 高效影像傳輸引擎 (Tile-based Video Engine)
- **64x64 變動區塊差異編碼**：僅傳輸有畫面更新的矩形區域。
- **動態雙層壓縮**：畫面移動時採用高速平行 JPEG 編碼保證極致流暢度；畫面靜止時自動以 Deflate/無損演算法補送高清區塊，文字與代碼清晰銳利。
- **流量控制 (Flow-Control ACK)**：限制在途未確認幀數，徹底避免網路抖動造成延遲累積。

---

## 專案結構

```
remote/
├── src/
│   └── RemoteDesk/
│       ├── Common/             # 共用協定、設定檔、加密與通訊通道
│       │   ├── AppConfig.cs     # 9 位數 ID 產生、DPAPI、設定檔持久化
│       │   ├── Log.cs           # 執行緒安全檔案日誌
│       │   ├── Protocol.cs      # 二進位封包序列化、IME 狀態結構
│       │   └── SecureChannel.cs # ECDH P-256、HKDF、AES-256-GCM 通道
│       ├── Host/               # 被控端核心
│       │   ├── AccessRequestDialog.cs # 互動授權彈窗
│       │   ├── CursorTracker.cs # 游標圖示抓取
│       │   ├── DesktopHelper.cs # 安全桌面 / UAC 執行緒桌面切換
│       │   ├── FrameEncoder.cs  # 64x64 差異區塊編碼器
│       │   ├── HostServer.cs    # TCP 監聽服務、驗證流程
│       │   ├── HostSession.cs   # 被控端串流與輸入處理
│       │   ├── ImeController.cs # IMM32 / TSF 狀態操控
│       │   ├── InputInjector.cs # 低階 SendInput 事件注入
│       │   ├── MonitorEnumerator.cs # 多螢幕列舉與偵測
│       │   ├── PasswordDialog.cs # 密碼設定視窗
│       │   └── ScreenCapturer.cs # DXGI + GDI 雙引擎畫面擷取
│       ├── Native/             # Win32 P/Invoke API 宣告
│       │   └── Win32.cs
│       ├── Net/                # 區域網路探索
│       │   └── LanDiscovery.cs  # UDP 廣播、裝置發現與動態 IP 解析
│       ├── Viewer/             # 控制端核心
│       │   ├── ConnectPasswordDialog.cs # 連線密碼輸入對話盒
│       │   ├── DesktopCanvas.cs # 高效能雙緩衝桌面畫布
│       │   ├── FrameDecoder.cs  # 影像區塊解碼器
│       │   ├── ImeSyncClient.cs # 輸入法雙向鎖定綁定客戶端
│       │   ├── KeyboardHook.cs  # 全域低階鍵盤 Hook
│       │   ├── ViewerForm.cs    # 控制端完整操作視窗
│       │   └── ViewerSession.cs # 控制端網路工作階段
│       ├── app.manifest        # DPI-Aware 與 Windows 權限資訊
│       ├── MainForm.cs         # 主程式現代化使用者介面
│       └── Program.cs          # 程式進入點與命令列支援
├── tests/
│   └── RemoteDesk.Tests/       # 自動化單元與整合測試
│       ├── CryptoAndChannelTests.cs
│       ├── EndToEndLoopbackTests.cs
│       ├── FrameEncoderDecoderTests.cs
│       ├── ImeStateTests.cs
│       ├── LanDiscoveryTests.cs
│       ├── MonitorTests.cs
│       └── ProtocolTests.cs
└── RemoteDesk.sln
```

---

## 建置與執行說明

### 系統需求
- 作業系統：Windows 10 / Windows 11 / Windows Server 2019+
- 執行環境：.NET 9.0 SDK
- 架構：x64

### 編譯專案
```powershell
# 建置整個解決方案
dotnet build RemoteDesk.sln

# 執行所有單元與端對端整合測試 (15 項測試全部通過)
dotnet test

# 發行單一執行檔版本
dotnet publish src\RemoteDesk\RemoteDesk.csproj -c Release -r win-x64 --no-self-contained -o publish
```

### 啟動程式
發行後的二進位檔案位於 `publish\RemoteDesk.exe`：
1. **直接執行**：
   ```powershell
   .\publish\RemoteDesk.exe
   ```
   主視窗將自動啟動本機被控端服務（預設連接埠 21119），同時在右側搜尋區域網路內的其他電腦。
2. **快速連線參數**：
   ```powershell
   # 直接透過 9 位數 ID 或 IP 連線至目標電腦
   .\publish\RemoteDesk.exe --connect 123456789
   .\publish\RemoteDesk.exe --connect 192.168.1.50
   ```
3. **無人值守被控服務模式**：
   ```powershell
   .\publish\RemoteDesk.exe --headless
   ```

---

## 內網多機連線操作說明

1. **被控端設定**：
   - 在被控電腦開啟 `RemoteDesk.exe`，左側會顯示該電腦唯一的 **9 位數 ID**（如 `456 789 123`）。
   - 若要無人值守，點擊「設定無人值守連線密碼」儲存專用密碼。
   - 若不設密碼，當有人發起連線時，被控電腦螢幕會跳出連線確認視窗，核對 6 位數確認碼後按「接受」即可。
2. **控制端連線**：
   - 在控制端電腦開啟 `RemoteDesk.exe`。
   - 「區域網路中已偵測到的電腦」清單會自動出現同一內網的所有電腦，直接雙擊該列；或在輸入框貼上對方的 9 位數 ID 後按「開始連線」。
3. **視窗操控**：
   - 頂部工具列可自由切換不同螢幕或合併檢視。
   - 工具列清楚顯示當前「輸入法綁定狀態」，兩端中/英狀態完全鎖定同步。
   - 支援全螢幕（F11）、視窗縮放比率與特殊指令（Ctrl+Alt+Del、鎖定螢幕等）。
