using System.Drawing;

namespace RemoteDesk.Common;

/// <summary>
/// 遠端桌面軟體設計系統 Design Tokens (依據規格第 2 節)：
/// 提供深色 (Dark) 與淺色 (Light) 主題語意 Token、標準字體與元件度量。
/// </summary>
public static class Theme
{
    // =========================================================================
    // 1. 深色主題語意色彩 (Dark Theme - 預設)
    // =========================================================================
    public static readonly Color Background = ColorTranslator.FromHtml("#111827");
    public static readonly Color Surface = ColorTranslator.FromHtml("#1F2937");
    public static readonly Color SurfaceElevated = ColorTranslator.FromHtml("#273449");

    public static readonly Color Primary = ColorTranslator.FromHtml("#2563EB");
    public static readonly Color PrimaryHover = ColorTranslator.FromHtml("#1D4ED8");
    public static readonly Color Accent = ColorTranslator.FromHtml("#60A5FA");

    public static readonly Color Success = ColorTranslator.FromHtml("#10B981");
    public static readonly Color Warning = ColorTranslator.FromHtml("#F59E0B");
    public static readonly Color Danger = ColorTranslator.FromHtml("#EF4444");

    public static readonly Color TextPrimary = ColorTranslator.FromHtml("#F9FAFB");
    public static readonly Color TextSecondary = ColorTranslator.FromHtml("#9CA3AF");
    public static readonly Color Border = ColorTranslator.FromHtml("#374151");

    // =========================================================================
    // 2. 淺色主題語意色彩 (Light Theme)
    // =========================================================================
    public static readonly Color LightBackground = ColorTranslator.FromHtml("#F3F4F6");
    public static readonly Color LightSurface = ColorTranslator.FromHtml("#FFFFFF");
    public static readonly Color LightSurfaceElevated = ColorTranslator.FromHtml("#F9FAFB");
    public static readonly Color LightPrimary = ColorTranslator.FromHtml("#2563EB");
    public static readonly Color LightTextPrimary = ColorTranslator.FromHtml("#111827");
    public static readonly Color LightTextSecondary = ColorTranslator.FromHtml("#6B7280");
    public static readonly Color LightBorder = ColorTranslator.FromHtml("#E5E7EB");

    // =========================================================================
    // 3. 字體家族與規格尺寸
    // =========================================================================
    public const string FontFamily = "Segoe UI";
    public const string FallbackFontFamily = "Microsoft JhengHei";

    public static Font CreateFont(float size, FontStyle style = FontStyle.Regular)
    {
        try
        {
            return new Font(FontFamily, size, style);
        }
        catch
        {
            return new Font(FallbackFontFamily, size, style);
        }
    }

    public static Font CreateMonoFont(float size, FontStyle style = FontStyle.Regular)
    {
        return new Font("Consolas", size, style);
    }

    // 常用文字字體
    public static Font PageTitleFont => CreateFont(16f, FontStyle.Bold);       // 區塊/頁面標題 16-18px
    public static Font SectionTitleFont => CreateFont(12.5f, FontStyle.Bold);  // 16px
    public static Font BodyFont => CreateFont(9.5f, FontStyle.Regular);        // 13-14px
    public static Font SmallFont => CreateFont(8.5f, FontStyle.Regular);       // 12px 輔助文字
    public static Font ButtonFont => CreateFont(9.5f, FontStyle.Bold);         // 13-14px 按鈕
    public static Font DeviceIdDisplayFont => CreateMonoFont(32f, FontStyle.Bold); // 本機設備 ID 放大
    public static Font CardIdFont => CreateMonoFont(11f, FontStyle.Bold);      // 設備卡片 ID

    // =========================================================================
    // 4. 元件度量與間距 (4px 基礎單位)
    // =========================================================================
    public const int BaseUnit = 4;
    public const int CornerRadius = 10;            // 8-12px (卡片與輸入框圓角)
    public const int InputHeight = 50;             // 48-54px
    public const int ButtonHeight = 38;            // 36-42px
    public const int HeaderHeight = 40;            // Custom Window Chrome 頂部標題列高度 (原 52px)
    public const int TitleBarHeight = 40;          // 頂部視窗控制列高度
    public const int ToolbarHeight = 42;           // 遠端工作階段次行工具列高度 (下一行)
    public const int MinWindowWidth = 720;
    public const int MinWindowHeight = 500;
    public const int DefaultWindowWidth = 1180;
    public const int DefaultWindowHeight = 780;
}
