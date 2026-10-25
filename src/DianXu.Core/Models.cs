namespace DianXu.Core;

public enum ClickButton { Left, Right }
public enum WaitUnit { Milliseconds, Seconds }

public sealed record ClickPoint(int X, int Y, ClickButton Button, decimal WaitValue, WaitUnit Unit)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan Delay => TimeSpan.FromMilliseconds((double)(WaitValue * (Unit == WaitUnit.Seconds ? 1000m : 1m)));

    public string? GetError()
    {
        if (!Enum.IsDefined(Button) || !Enum.IsDefined(Unit)) return "鼠标按键或时间单位无效。";
        // Bound before multiplication so malformed settings cannot overflow decimal/TimeSpan.
        if (WaitValue <= 0 || WaitValue > 86_400_000m) return "等待时间必须大于零，且不超过 24 小时。";
        decimal ms = WaitValue * (Unit == WaitUnit.Seconds ? 1000m : 1m);
        return ms < 1m || ms > 86_400_000m ? "等待时间须在 1 毫秒到 24 小时之间。" : null;
    }
}

public sealed record ClickPlan(IReadOnlyList<ClickPoint> Points, long? Limit)
{
    public void Validate()
    {
        if (Points.Count == 0) throw new ArgumentException("请先添加至少一个点击位置。");
        if (Limit is <= 0) throw new ArgumentException("指定点击次数必须是大于零的整数。");
        for (int i = 0; i < Points.Count; i++)
            if (Points[i].GetError() is string error) throw new ArgumentException($"第 {i + 1} 个位置：{error}");
    }
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public List<ClickPoint> Points { get; set; } = [];
    public bool Limited { get; set; }
    public long ClickLimit { get; set; } = 100;
    public int ToggleKey { get; set; } = 0x75;
    public int EmergencyKey { get; set; } = 0x77;
    public bool MinimizeOnStart { get; set; } = true;

    public void Validate()
    {
        if (Version != 1 || Points is null) throw new FormatException("设置文件版本或结构不受支持。");
        if (Points.Any(p => p is null || p.GetError() is not null)) throw new FormatException("设置文件包含无效的点击位置。");
        if (ClickLimit <= 0) throw new FormatException("保存的点击次数无效。");
        if (!IsSupportedKey(ToggleKey) || !IsSupportedKey(EmergencyKey) || ToggleKey == EmergencyKey)
            throw new FormatException("保存的快捷键无效。");
    }

    public static bool IsSupportedKey(int key) => key >= 0x70 && key <= 0x7B; // F1–F12.
    public static string KeyName(int key) => $"F{key - 0x6F}";
}
