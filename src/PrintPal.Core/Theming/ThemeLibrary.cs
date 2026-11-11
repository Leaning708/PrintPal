using System.Text.Json;

namespace PrintPal.Core.Theming;

/// <summary>
/// 自定义主题库：多个命名自定义主题的增删改查 + 当前选中项，整体以 JSON 存在 Settings 里。
/// 纯数据结构，App 负责应用与持久化。
/// </summary>
public sealed class ThemeLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public int Version { get; set; } = 1;

    public List<ThemeCustomization> Themes { get; set; } = new();

    /// <summary>当前选中的自定义主题 id（可能为空）。</summary>
    public string? SelectedId { get; set; }

    public static ThemeLibrary CreateDefault() => new();

    /// <summary>把旧版单条自定义配色迁移成主题库（旧数据只有一条，名字用「自定义」）。</summary>
    public static ThemeLibrary FromLegacy(ThemeCustomization? legacy)
    {
        var library = new ThemeLibrary();
        if (legacy is null)
        {
            return library;
        }

        var normalized = legacy.Normalized();
        if (string.IsNullOrWhiteSpace(normalized.Name) || normalized.Name == "自定义")
        {
            normalized.Name = "自定义";
        }

        library.Themes.Add(normalized);
        library.SelectedId = normalized.Id;
        return library;
    }

    public ThemeCustomization? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return Themes.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>用种子新建一个主题，返回新主题 id。</summary>
    public string Add(ThemeCustomization seed, string? name)
    {
        ArgumentNullException.ThrowIfNull(seed);

        var created = new ThemeCustomization
        {
            Version = ThemeCustomization.CurrentVersion,
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? NextDefaultName() : name.Trim(),
            IsDark = seed.IsDark,
            Accent = seed.Accent,
            PageStyle = seed.PageStyle,
            PageColor = seed.PageColor,
            SidebarStyle = seed.SidebarStyle,
            Overrides = seed.Overrides is null
                ? null
                : new Dictionary<string, string>(seed.Overrides, StringComparer.OrdinalIgnoreCase),
        }.Normalized();

        Themes.Add(created);
        SelectedId = created.Id;
        return created.Id;
    }

    /// <summary>按 id 覆盖更新；id 不存在时返回 false。</summary>
    public bool Update(ThemeCustomization theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        var index = Themes.FindIndex(item =>
            string.Equals(item.Id, theme.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        Themes[index] = theme.Normalized();
        SelectedId = theme.Id;
        return true;
    }

    public bool Remove(string? id)
    {
        if (Find(id) is not { } theme)
        {
            return false;
        }

        Themes.Remove(theme);
        if (string.Equals(SelectedId, id, StringComparison.OrdinalIgnoreCase))
        {
            SelectedId = Themes.FirstOrDefault()?.Id;
        }

        return true;
    }

    public bool Select(string? id)
    {
        if (Find(id) is null)
        {
            return false;
        }

        SelectedId = id;
        return true;
    }

    /// <summary>生成一个不与现有主题重名的默认名称。</summary>
    public string NextDefaultName()
    {
        for (var i = 1; i < 1000; i++)
        {
            var name = $"自定义主题 {i}";
            if (!Themes.Any(theme => string.Equals(theme.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return name;
            }
        }

        return "自定义主题";
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>解析失败返回空库（不抛异常），调用方按"没有自定义主题"处理。</summary>
    public static ThemeLibrary FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return CreateDefault();
        }

        try
        {
            return JsonSerializer.Deserialize<ThemeLibrary>(json, JsonOptions) ?? CreateDefault();
        }
        catch (JsonException)
        {
            return CreateDefault();
        }
    }
}
