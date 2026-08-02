using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AudioDeviceConfigurator.Abstractions;

namespace AudioDeviceConfigurator.Windows;

public sealed class Win32ControlApi : IWin32ControlApi
{
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint SmtoBlock = 0x0001;
    private const uint TcmGetItemCount = 0x1304;
    private const uint TcmGetItemA = 0x1305;
    private const uint TcmGetCurSel = 0x130B;
    private const uint TcmSetCurSel = 0x130C;
    private const uint WmNotify = 0x004E;
    private const int TcnSelChange = -551;
    private const uint TcifText = 0x0001;

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumChildProc callback, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageNative(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags,
        uint timeout, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "IsWindowUnicode")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowUnicodeNative(IntPtr handle);

    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowNative(IntPtr handle);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisibleNative(IntPtr handle);

    [DllImport("user32.dll", EntryPoint = "IsWindowEnabled")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabledNative(IntPtr handle);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr handle, StringBuilder className, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetClassNameA", ExactSpelling = true, SetLastError = true)]
    private static extern int GetClassNameA(IntPtr handle, byte[] className, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextA", ExactSpelling = true, SetLastError = true)]
    private static extern int GetWindowTextA(IntPtr handle, byte[] text, int maxCount);

    [DllImport("kernel32.dll", EntryPoint = "GetSystemDefaultLCID")]
    private static extern uint GetSystemDefaultLCID();
    [DllImport("kernel32.dll", EntryPoint = "GetLocaleInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetLocaleInfoW(uint locale, uint type, StringBuilder data, int dataLength);
    [DllImport("kernel32.dll", EntryPoint = "GetACP")]
    private static extern uint GetACP();

    [DllImport("kernel32.dll", EntryPoint = "MultiByteToWideChar", ExactSpelling = true, SetLastError = true)]
    private static extern int MultiByteToWideChar(uint codePage, uint flags, byte[] multiByte, int length, IntPtr wideChar, int wideLength);

    private static readonly Lazy<uint> LegacyCodePage = new(GetLegacySystemAnsiCodePage);
    private const uint LocaleIDefaultAnsiCodePage = 0x1004;

    private static uint GetLegacySystemAnsiCodePage()
    {
        var buffer = new StringBuilder(16);
        var length = GetLocaleInfoW(GetSystemDefaultLCID(), LocaleIDefaultAnsiCodePage, buffer, buffer.Capacity);
        return length > 0 && uint.TryParse(buffer.ToString(), out var codePage) && codePage > 0 ? codePage : GetACP();
    }


    private static string DecodeAnsiWindows(byte[] bytes, int length)
    {
        if (length <= 0) return string.Empty;
        var buffer = Marshal.AllocHGlobal((length + 1) * sizeof(char));
        try
        {
            var codePage = LegacyCodePage.Value;
            var converted = MultiByteToWideChar(codePage, 0x00000008, bytes, length, buffer, length + 1);
            if (converted == 0) converted = MultiByteToWideChar(codePage, 0, bytes, length, buffer, length + 1);
            return converted > 0 ? (Marshal.PtrToStringUni(buffer, converted) ?? string.Empty).TrimEnd('\0') : string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcItem
    {
        public uint Mask;
        public int State;
        public int StateMask;
        public IntPtr Text;
        public int TextMax;
        public int Image;
        public IntPtr Param;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NmHdr
    {
        public IntPtr HwndFrom;
        public IntPtr IdFrom;
        public IntPtr Code;
    }


    public bool IsWindow(IntPtr handle) => IsWindowNative(handle);

    public bool IsWindowUnicode(IntPtr handle) => IsWindowUnicodeNative(handle);

    public bool TrySelectAdvancedTab(IntPtr dialog, TimeSpan timeout, out string observed)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        var tabs = EnumerateChildWindows(dialog).Where(handle => GetClassNameText(handle) == "SysTabControl32").ToArray();
        var names = new List<string>();
        foreach (var tab in tabs)
        {
            if (!TrySendMessageTimeout(tab, TcmGetItemCount, IntPtr.Zero, IntPtr.Zero, Remaining(deadline), out var countValue)) continue;
            var count = countValue.ToInt64();
            if (count < 0 || count > 128) continue;
            for (var index = 0; index < count; index++)
            {
                if (!TryGetTabText(tab, index, Remaining(deadline), out var name)) continue;
                names.Add(name);
                if (!string.Equals(name, "Advanced", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "進階", StringComparison.OrdinalIgnoreCase)) continue;
                if (!TrySendMessageTimeout(tab, TcmSetCurSel, (IntPtr)index, IntPtr.Zero, Remaining(deadline), out _)) continue;
                if (!TrySendMessageTimeout(tab, TcmGetCurSel, IntPtr.Zero, IntPtr.Zero, Remaining(deadline), out var selected)
                    || selected.ToInt64() != index) continue;
                var notification = new NmHdr { HwndFrom = tab, Code = (IntPtr)TcnSelChange };
                var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NmHdr>());
                try
                {
                    Marshal.StructureToPtr(notification, pointer, false);
                    TrySendMessageTimeout(dialog, WmNotify, IntPtr.Zero, pointer, Remaining(deadline), out _);
                }
                finally { Marshal.FreeHGlobal(pointer); }
                observed = string.Join("; ", names);
                return true;
            }
        }

        observed = string.Join("; ", names);
        return false;
    }

    private static string GetClassNameText(IntPtr handle)
    {
        var wide = new StringBuilder(256);
        var ansi = new byte[256];
        var wLength = GetClassNameW(handle, wide, wide.Capacity);
        var aLength = GetClassNameA(handle, ansi, ansi.Length);
        return ChooseWindowText(wide.ToString(0, Math.Max(0, wLength)), DecodeAnsiWindows(ansi, Math.Max(0, aLength)), IsWindowUnicodeNative(handle));
    }

    private static TimeSpan Remaining(long deadline)
    {
        var ticks = deadline - Stopwatch.GetTimestamp();
        return TimeSpan.FromSeconds(Math.Max(0.001, ticks / (double)Stopwatch.Frequency));
    }

    private bool TryGetTabText(IntPtr tab, long index, TimeSpan timeout, out string text)
    {
        var buffer = Marshal.AllocHGlobal(512);
        var item = new TcItem { Mask = TcifText, Text = buffer, TextMax = 256 };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TcItem>());
        try
        {
            Marshal.StructureToPtr(item, pointer, false);
            if (!TrySendMessageTimeout(tab, TcmGetItemA, (IntPtr)index, pointer, timeout, out var result)
                || result == IntPtr.Zero)
            {
                text = string.Empty;
                return false;
            }
            text = Marshal.PtrToStringAnsi(buffer) ?? string.Empty;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
            Marshal.FreeHGlobal(buffer);
        }
    }

    public bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam) =>
        handle != IntPtr.Zero && PostMessageNative(handle, message, wParam, lParam);

    public bool TrySendMessageTimeout(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam,
        TimeSpan timeout, out IntPtr result)
    {
        if (handle == IntPtr.Zero || timeout <= TimeSpan.Zero)
        {
            result = IntPtr.Zero;
            return false;
        }

        var milliseconds = (uint)Math.Clamp(timeout.TotalMilliseconds, 1, uint.MaxValue);
        return SendMessageTimeout(handle, message, wParam, lParam,
            SmtoAbortIfHung | SmtoBlock, milliseconds, out result) != IntPtr.Zero;
    }

    public bool TryReadComboBoxItems(IntPtr combo, TimeSpan timeout, out IReadOnlyList<string> items, out string diagnostic)
    {
        const uint CbGetCount = 0x0146, CbGetLbTextLen = 0x0149, CbGetLbText = 0x0148;
        items = Array.Empty<string>(); diagnostic = string.Empty;
        var firstCallTimeout = timeout > TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : timeout;
        if (!TrySendMessageTimeout(combo, CbGetCount, IntPtr.Zero, IntPtr.Zero, firstCallTimeout, out var countValue)) { diagnostic = "CB_GETCOUNT"; return false; }
        var count = countValue.ToInt64(); if (count < 1 || count > 512) { diagnostic = $"count={count}"; return false; }
        var unicode = IsWindowUnicodeNative(combo); var result = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var callTimeout = timeout > TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : timeout;
            if (!TrySendMessageTimeout(combo, CbGetLbTextLen, (IntPtr)index, IntPtr.Zero, callTimeout, out var lengthValue)) { diagnostic = $"CB_GETLBTEXTLEN[{index}]"; return false; }
            var length = lengthValue.ToInt64(); if (length < 0 || length > 4096) { diagnostic = $"len={length}"; return false; }
            var bytes = checked((int)Math.Max((length + 1) * 2, length + 1));
            var memory = Marshal.AllocHGlobal(bytes);
            try
            {
                var zero = new byte[bytes]; Marshal.Copy(zero, 0, memory, bytes);
                if (!TrySendMessageTimeout(combo, CbGetLbText, (IntPtr)index, memory, callTimeout, out var textResult)
                    || textResult == (IntPtr)(-1)) { diagnostic = $"CB_GETLBTEXT[{index}] result={textResult.ToInt64()}"; return false; }
                var returned = Math.Clamp(textResult.ToInt64(), 0, length);
                var unicodeText = Marshal.PtrToStringUni(memory, (int)returned) ?? string.Empty;
                var ansiBytes = new byte[bytes]; Marshal.Copy(memory, ansiBytes, 0, bytes);
                var ansiLength = checked((int)returned);
                while (ansiLength < bytes && ansiBytes[ansiLength] != 0) ansiLength++;
                var ansi = DecodeAnsiWindows(ansiBytes, ansiLength).TrimEnd('\0');
                result.Add(ChooseFormatText(unicodeText, ansi, IsWindowUnicodeNative(combo)));
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        items = result; return true;
    }

    public bool TryReadFormatItemsAcrossTabs(IntPtr dialog, TimeSpan timeout, out Win32FormatReadResult result, out string observed)
    {
        const uint TcmGetItemCount = 0x1304;
        const uint TcmGetCurSel = 0x130B;
        const uint PsmSetCurSel = 0x0465;
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        result = new Win32FormatReadResult(-1, -1, Array.Empty<string>());
        var notes = new List<string>();
        foreach (var tab in EnumerateChildWindows(dialog).Where(handle => GetClassNameText(handle) == "SysTabControl32"))
        {
            var tabTimeout = Remaining(deadline);
            if (tabTimeout > TimeSpan.FromMilliseconds(250)) tabTimeout = TimeSpan.FromMilliseconds(250);
            if (!TrySendMessageTimeout(tab, TcmGetItemCount, IntPtr.Zero, IntPtr.Zero, tabTimeout, out var countValue)
                || !TrySendMessageTimeout(tab, TcmGetCurSel, IntPtr.Zero, IntPtr.Zero, tabTimeout, out var originalValue))
            {
                notes.Add($"tabHandle={tab.ToInt64()};count=timeout");
                continue;
            }
            var count = countValue.ToInt64();
            notes.Add($"tabHandle={tab.ToInt64()};count={count}");
            var original = originalValue.ToInt32();
            if (count < 1 || count > 32) continue;
            try
            {
                for (var index = 0; index < count && Stopwatch.GetTimestamp() < deadline; index++)
                {
                    if (!PostMessage(tab, TcmSetCurSel, (IntPtr)index, IntPtr.Zero)
                        || !PostMessage(dialog, PsmSetCurSel, (IntPtr)index, IntPtr.Zero))
                    {
                        notes.Add($"tab={index};psm=failed");
                        continue;
                    }
                    var pageResult = (IntPtr)1;
                    Thread.Sleep(75);
                    var selectedValue = IntPtr.Zero;
                    var selectionDeadline = Stopwatch.GetTimestamp() + (long)(TimeSpan.FromMilliseconds(500).TotalSeconds * Stopwatch.Frequency);
                    while (Stopwatch.GetTimestamp() < selectionDeadline)
                    {
                        var selectionTimeout = TimeSpan.FromMilliseconds(100);
                        if (TrySendMessageTimeout(tab, TcmGetCurSel, IntPtr.Zero, IntPtr.Zero, selectionTimeout, out selectedValue)
                            && selectedValue.ToInt32() == index) break;
                        Thread.Sleep(25);
                    }
                    notes.Add($"tab={index};selected={selectedValue.ToInt32()}");
                    var comboCount = 0;
                    foreach (var combo in EnumerateChildWindows(dialog).Where(handle => GetClassNameText(handle) == "ComboBox" && IsWindowVisibleNative(handle) && IsWindowEnabledNative(handle)))
                    {
                        comboCount++;
                        var comboCountValue = IntPtr.Zero;
                        var probeTimeout = Remaining(deadline);
                        if (probeTimeout > TimeSpan.FromMilliseconds(250)) probeTimeout = TimeSpan.FromMilliseconds(250);
                        TrySendMessageTimeout(combo, 0x0146, IntPtr.Zero, IntPtr.Zero, probeTimeout, out comboCountValue);
                        var lengthValue = IntPtr.Zero;
                        if (comboCountValue.ToInt64() > 0) TrySendMessageTimeout(combo, 0x0149, IntPtr.Zero, IntPtr.Zero, probeTimeout, out lengthValue);
                        if (!TryReadCombo(combo, probeTimeout, out var items, out var comboDiagnostic) || items.Count == 0)
                        {
                            notes.Add($"tab={index};combo={combo.ToInt64()};unicode={IsWindowUnicodeNative(combo)};count={comboCountValue.ToInt64()};len0={lengthValue.ToInt64()};text={comboDiagnostic}");
                            continue;
                        }
                        var itemSummary = string.Join("|", items.Take(5).Select(text => $"{Describe(text)}"));
                        var valid = items.All(text => text.Contains("Hz", StringComparison.OrdinalIgnoreCase)) && items.Any(text => text.Contains("16", StringComparison.Ordinal) || text.Contains("24", StringComparison.Ordinal));
                        notes.Add($"tab={index};combo={combo.ToInt64()};unicode={IsWindowUnicodeNative(combo)};count={items.Count};items={itemSummary};valid={valid}");
                        if (valid)
                        {
                            result = new Win32FormatReadResult(index, original, items);
                            observed = string.Join(",", notes);
                            return true;
                        }
                    }
                    notes.Add($"tab={index};psm={pageResult.ToInt64()};cur={index};combos={comboCount}");
                }
            }
            finally
            {
                if (original >= 0)
                {
                    PostMessage(tab, TcmSetCurSel, (IntPtr)original, IntPtr.Zero);
                    PostMessage(dialog, PsmSetCurSel, (IntPtr)original, IntPtr.Zero);
                }
            }
        }
        observed = string.Join(",", notes);
        return false;
    }

    internal static string ChooseFormatText(string unicode, string ansi, bool preferUnicode)
    {
        var unicodeScore = FormatScore(unicode);
        var ansiScore = FormatScore(ansi);
        if (unicodeScore > ansiScore) return unicode;
        if (ansiScore > unicodeScore) return ansi;
        if (unicodeScore > 0)
        {
            var unicodeReplacement = unicode.Count(c => c == '�');
            var ansiReplacement = ansi.Count(c => c == '�');
            if (unicodeReplacement != ansiReplacement) return unicodeReplacement < ansiReplacement ? unicode : ansi;
            return preferUnicode ? unicode : ansi;
        }
        return preferUnicode ? unicode : ansi;
    }

    internal static int FormatScore(string text)
    {
        var score = 0;
        if (text.Contains("Hz", StringComparison.OrdinalIgnoreCase)) score += 4;
        if (text.Any(c => c is '1' or '2' or '0') && (text.Contains("16") || text.Contains("20") || text.Contains("24"))) score += 3;
        if (text.Any(char.IsDigit)) score++;
        return score;
    }

    private static string Describe(string value)
    {
        var text = value.Length > 120 ? value[..120] : value;
        return text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal)
            + "[U+" + string.Join(" U+", text.EnumerateRunes().Select(r => r.Value.ToString("X"))) + "]";
    }

    private bool TryReadCombo(IntPtr combo, TimeSpan timeout, out IReadOnlyList<string> items, out string diagnostic)
    {
        return TryReadComboBoxItems(combo, timeout, out items, out diagnostic);
    }

    public bool IsWindowVisible(IntPtr handle) => IsWindowVisibleNative(handle);

    public string GetWindowClassName(IntPtr handle) => GetClassNameText(handle);

    public string GetWindowText(IntPtr handle) => GetWindowTitle(handle);

    public IReadOnlyList<Win32WindowInfo> EnumerateTopLevelWindows()
    {
        var result = new List<Win32WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            result.Add(new Win32WindowInfo(hwnd, GetClassNameText(hwnd), GetWindowTitle(hwnd), IsWindowVisibleNative(hwnd)));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        var wide = new StringBuilder(512);
        var ansi = new byte[512];
        var wLength = GetWindowTextW(handle, wide, wide.Capacity);
        var aLength = GetWindowTextA(handle, ansi, ansi.Length);
        return ChooseWindowText(wide.ToString(0, Math.Max(0, wLength)), DecodeAnsiWindows(ansi, Math.Max(0, aLength)), IsWindowUnicodeNative(handle));
    }

    internal static string ChooseWindowText(string wide, string ansi, bool preferWide)
    {
        static int Score(string text) =>
            (text.Contains("Sound", StringComparison.OrdinalIgnoreCase)
                || text.Contains("聲音", StringComparison.OrdinalIgnoreCase)
                || text.Contains("音效", StringComparison.OrdinalIgnoreCase) ? 20 : 0)
            + (text.Contains("設定", StringComparison.OrdinalIgnoreCase)
                || text.Contains("內容", StringComparison.OrdinalIgnoreCase)
                || text.Contains("屬性", StringComparison.OrdinalIgnoreCase)
                || text.Contains("預設", StringComparison.OrdinalIgnoreCase)
                || text.Contains("套用", StringComparison.OrdinalIgnoreCase)
                || text.Contains("取消", StringComparison.OrdinalIgnoreCase) ? 10 : 0)
            + (text.Count(c => c == '�') * -10)
            + (text.Count(char.IsControl) * -5)
            + (text.Length > 0 ? 1 : 0);
        var wideScore = Score(wide);
        var ansiScore = Score(ansi);
        if (wideScore != ansiScore) return wideScore > ansiScore ? wide : ansi;
        return preferWide ? wide : ansi;
    }

    public IReadOnlyList<IntPtr> EnumerateChildWindows(IntPtr parent)
    {
        var result = new List<IntPtr>();
        EnumChildWindows(parent, (hwnd, _) => { result.Add(hwnd); return true; }, IntPtr.Zero);
        return result;
    }
}
