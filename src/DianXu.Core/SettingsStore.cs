using System.Text.Json;
using System.Text.Json.Serialization;

namespace DianXu.Core;

public sealed record SettingsLoadResult(AppSettings Settings, string? Warning);
public sealed class SettingsStore(string directory)
{
    public string FilePath { get; } = Path.Combine(directory, "settings.json");
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SettingsLoadResult Load()
    {
        if (!File.Exists(FilePath)) return new(new AppSettings(), null);
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options)
                ?? throw new FormatException("设置文件内容为空。");
            settings.Validate();
            return new(settings, null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException or UnauthorizedAccessException)
        {
            string? backup = null;
            try
            {
                backup = FilePath + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                File.Copy(FilePath, backup, false);
            }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException) { backup = null; }
            string suffix = backup is null ? "原文件未被删除。" : "原文件已在同一文件夹备份。";
            return new(new AppSettings(), "无法读取上次设置，已恢复安全默认值，不会自动运行。" + suffix + "\n" + ex.Message);
        }
    }

    public void Save(AppSettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
