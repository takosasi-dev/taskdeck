namespace TaskDeck.App.Residency;

/// <summary>
/// グローバルホットキーの組み合わせ（設定の "Ctrl+Shift+Space"）。修飾キーは Win32 の MOD_* と同じ値
/// （WPF の ModifierKeys とも同じ値なので、画面側は (uint)Keyboard.Modifiers をそのまま渡せる）。
/// キーは仮想キーコード。名前を付けたキー（英字・数字・F1〜F24・テンキー・Space など）だけを扱い、
/// キーボードの配列で位置が変わる記号キーは扱わない（日本語配列と英語配列で名前がずれるため）。
/// </summary>
public readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey)
{
    public const uint Alt = 1;
    public const uint Control = 2;
    public const uint Shift = 4;
    public const uint Win = 8;

    private static readonly (string Name, uint Key)[] Named = BuildNames();

    private static readonly Dictionary<string, uint> KeyByName =
        Named.ToDictionary(n => n.Name, n => n.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<uint, string> NameByKey = Named.ToDictionary(n => n.Key, n => n.Name);

    /// <summary>Ctrl・Alt・Win のどれかを含むか（Shift だけでは文字入力を横取りするので、設定画面ではこれを求める）。</summary>
    public bool HasCommandModifier => (Modifiers & (Control | Alt | Win)) != 0;

    /// <summary>"Ctrl+Shift+Space" 形式を読む。修飾キーの別名（Control・Windows）と大文字小文字の違いは許す。</summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        uint modifiers = 0;
        uint? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            var modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => Control,
                "ALT" => Alt,
                "SHIFT" => Shift,
                "WIN" or "WINDOWS" => Win,
                _ => 0u,
            };
            if (modifier != 0)
            {
                modifiers |= modifier;
            }
            else if (key is null && KeyByName.TryGetValue(part, out var vk))
            {
                key = vk;
            }
            else
            {
                return false; // 知らない名前・キーが2つ・空の部分（"Ctrl++"）
            }
        }
        if (key is not { } found)
        {
            return false;
        }
        gesture = new HotkeyGesture(modifiers, found);
        return true;
    }

    /// <summary>押されたキーから作る。修飾キーだけ・名前の無いキーなら null。</summary>
    public static HotkeyGesture? FromKey(uint modifiers, uint virtualKey) =>
        NameByKey.ContainsKey(virtualKey) ? new HotkeyGesture(modifiers & (Control | Alt | Shift | Win), virtualKey) : null;

    /// <summary>設定に保存する形（"Ctrl+Shift+Space"）。</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if ((Modifiers & Control) != 0)
        {
            parts.Add("Ctrl");
        }
        if ((Modifiers & Alt) != 0)
        {
            parts.Add("Alt");
        }
        if ((Modifiers & Shift) != 0)
        {
            parts.Add("Shift");
        }
        if ((Modifiers & Win) != 0)
        {
            parts.Add("Win");
        }
        parts.Add(NameByKey.TryGetValue(VirtualKey, out var name) ? name : $"0x{VirtualKey:X2}");
        return string.Join('+', parts);
    }

    /// <summary>画面に出す形（"Ctrl + Shift + Space"）。読めない文字列はそのまま区切りだけ整える。</summary>
    public static string Display(string text) =>
        TryParse(text, out var gesture)
            ? gesture.ToString().Replace("+", " + ", StringComparison.Ordinal)
            : string.Join(" + ", text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static (string, uint)[] BuildNames()
    {
        var names = new List<(string, uint)>
        {
            ("Space", 0x20), ("Enter", 0x0D), ("Tab", 0x09), ("Backspace", 0x08),
            ("PageUp", 0x21), ("PageDown", 0x22), ("End", 0x23), ("Home", 0x24),
            ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28),
            ("Insert", 0x2D), ("Delete", 0x2E), ("Pause", 0x13),
        };
        for (var c = '0'; c <= '9'; c++)
        {
            names.Add((c.ToString(), c));
        }
        for (var c = 'A'; c <= 'Z'; c++)
        {
            names.Add((c.ToString(), c));
        }
        for (uint i = 0; i <= 9; i++)
        {
            names.Add(($"Num{i}", 0x60 + i));
        }
        for (uint i = 1; i <= 24; i++)
        {
            names.Add(($"F{i}", 0x6F + i));
        }
        return [.. names];
    }
}
