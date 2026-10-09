using System.Windows.Input;

namespace Nighty.Models;

/// <summary>Hotkey modifier bits (same values as RegisterHotKey) and display helpers.</summary>
public static class Hotkeys
{
    public const int Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

    public static string KeyName(int vk)
    {
        if (vk <= 0) return "Not set";
        if (vk == 0x04) return "Middle Mouse";
        if (vk == 0x05) return "Mouse Side 1 (Back)";
        if (vk == 0x06) return "Mouse Side 2 (Forward)";
        var key = KeyInterop.KeyFromVirtualKey(vk);
        if (key == Key.None) return $"0x{vk:X2}";
        return key switch
        {
            Key.Return => "Enter",
            Key.Prior => "Page Up",
            Key.Next => "Page Down",
            Key.Back => "Backspace",
            Key.Capital => "Caps Lock",
            Key.Oem3 => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.LeftShift or Key.RightShift => "Shift",
            Key.LeftCtrl or Key.RightCtrl => "Ctrl",
            Key.LeftAlt or Key.RightAlt => "Alt",
            >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
            _ => key.ToString(),
        };
    }

    public static string Format(int vk, int mods)
    {
        if (vk <= 0) return "Not set";
        var parts = new List<string>();
        if ((mods & Ctrl) != 0) parts.Add("Ctrl");
        if ((mods & Alt) != 0) parts.Add("Alt");
        if ((mods & Shift) != 0) parts.Add("Shift");
        if ((mods & Win) != 0) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join(" + ", parts);
    }
}
