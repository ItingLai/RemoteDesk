using RemoteDesk.Common;
using Xunit;

namespace RemoteDesk.Tests;

public class ImeStateTests
{
    [Fact]
    public void TestChineseVsEnglishDetection()
    {
        // 繁體中文注音 (LangId: 0x0404, Open: true, IME_CMODE_NATIVE: 1)
        var chineseState = new ImeState(0x04040404, Open: true, Conversion: 0x0001);
        Assert.Equal(0x0404, chineseState.LangId);
        Assert.True(chineseState.IsNative);

        // 繁體中文但切換到英文輸入狀態 (Open: true 但 conversion mode 為 0，或 Open: false)
        var englishInChineseIme = new ImeState(0x04040404, Open: true, Conversion: 0x0000);
        Assert.False(englishInChineseIme.IsNative);

        var imeClosed = new ImeState(0x04040404, Open: false, Conversion: 0x0001);
        Assert.False(imeClosed.IsNative);

        // 美式英文鍵盤 (LangId: 0x0409)
        var usEnglish = new ImeState(0x04090409, Open: false, Conversion: 0);
        Assert.Equal(0x0409, usEnglish.LangId);
        Assert.False(usEnglish.IsNative);
    }

    [Fact]
    public void TestSameModeLogic()
    {
        var s1 = new ImeState(0x04040404, Open: true, Conversion: 0x0001);
        var s2 = new ImeState(0x04040404, Open: true, Conversion: 0x0001);
        Assert.True(s1.SameMode(s2));

        // 一中一英：應該回傳 false，觸發同步切換！
        var sEnglish = new ImeState(0x04040404, Open: true, Conversion: 0x0000);
        Assert.False(s1.SameMode(sEnglish));

        // 語系不同：應該回傳 false，觸發切換！
        var sDifferentLang = new ImeState(0x04090409, Open: true, Conversion: 0x0001);
        Assert.False(s1.SameMode(sDifferentLang));
    }
}
