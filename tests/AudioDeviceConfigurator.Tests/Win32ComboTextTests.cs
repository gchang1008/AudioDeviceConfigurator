using System.Runtime.InteropServices;
using AudioDeviceConfigurator.Windows;

namespace AudioDeviceConfigurator.Tests;

public sealed class Win32ComboTextTests
{
    [Fact]
    public void Decodes_unicode_combo_text_exactly()
    {
        var memory = Marshal.StringToCoTaskMemUni("2 聲道, 48000 Hz, 24 位元");
        try { Assert.Equal("2 聲道, 48000 Hz, 24 位元", ControlPanelFormatProvider.DecodeComboText(memory, true)); }
        finally { Marshal.FreeCoTaskMem(memory); }
    }

    [Fact]
    public void Decodes_ansi_traditional_chinese_combo_text_exactly()
    {
        var memory = Marshal.StringToCoTaskMemAnsi("2 聲道, 48000 Hz, 24 位元");
        try { Assert.Equal("2 聲道, 48000 Hz, 24 位元", ControlPanelFormatProvider.DecodeComboText(memory, false)); }
        finally { Marshal.FreeCoTaskMem(memory); }
    }

    [Fact]
    public void Decodes_the_five_expected_control_panel_items_exactly()
    {
        var expected = new[]
        {
            "16 位元，32000 Hz (調頻廣播品質)",
            "16 位元，44100 Hz (CD 音質)",
            "16 位元，48000 Hz (DVD 品質)",
            "24 位元，44100 Hz (錄音室品質)",
            "24 位元，48000 Hz (錄音室品質)",
        };
        var actual = expected.Select(value =>
        {
            var memory = Marshal.StringToCoTaskMemAnsi(value);
            try { return ControlPanelFormatProvider.DecodeComboText(memory, false); }
            finally { Marshal.FreeCoTaskMem(memory); }
        }).ToArray();
        Assert.Equal(expected, actual);
    }
}
