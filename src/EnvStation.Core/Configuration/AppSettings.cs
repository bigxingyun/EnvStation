using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnvStation.Core.Configuration;

/// <summary>主题选择。</summary>
/// <remarks>
/// 三态而不是两态：上一版只有"深/浅"对翻，且首次点击必然跳到深色
/// （初始值是"跟随系统"，而取反逻辑把它当成了浅色）。三态是需求的原本设计（规范 TH-2）。
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ThemeChoice>))]
public enum ThemeChoice
{
    /// <summary>跟随系统（首次启动的默认值）。</summary>
    System = 0,

    /// <summary>强制深色。</summary>
    Dark = 1,

    /// <summary>强制浅色。</summary>
    Light = 2,
}

/// <summary>
/// 需要跨次启动保留的设置。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：整个界面工程此前<b>没有任何设置持久化</b>——主题、窗口尺寸、上次所在页面
/// 重启即丢（重构方案 4.3 节）。"设置页"上写的「深色 / 浅色，右上角切换」甚至是一句指路说明，
/// 而不是一个控件。
/// </para>
/// <para>
/// 字段刻意保持少而平：设置项越多，"上次用的是哪个"就越容易变成用户想不起来的状态。
/// 这里只放四类：主题、上次所在页面、窗口几何、以及一个供将来使用的密度档位。
/// </para>
/// </remarks>
public sealed record AppSettings
{
    /// <summary>当前设置格式版本。将来不兼容变更时据此迁移。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>格式版本。</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>主题选择。</summary>
    public ThemeChoice Theme { get; init; } = ThemeChoice.System;

    /// <summary>上次所在页面的标识（如 <c>doctor</c>）；空表示用默认页。</summary>
    public string LastPage { get; init; } = string.Empty;

    /// <summary>窗口宽度（像素）；0 表示用默认值。</summary>
    public int WindowWidth { get; init; }

    /// <summary>窗口高度（像素）；0 表示用默认值。</summary>
    public int WindowHeight { get; init; }

    /// <summary>窗口左上角 X；null 表示交给系统放置。</summary>
    public int? WindowX { get; init; }

    /// <summary>窗口左上角 Y；null 表示交给系统放置。</summary>
    public int? WindowY { get; init; }

    /// <summary>默认设置。</summary>
    public static AppSettings Default { get; } = new();

    /// <summary>
    /// 把窗口几何限制在合理范围内。
    /// </summary>
    /// <remarks>
    /// 必须做这一步：显示器拔掉、分辨率变小、或者设置文件被手工改过之后，
    /// 一个 5000×3000 的窗口或位于已不存在屏幕上的坐标会让应用"启动后看不见"——
    /// 用户唯一的办法是删掉设置文件，而他还不知道有这么一个文件。
    /// </remarks>
    public AppSettings Clamp(int minWidth = 800, int minHeight = 600, int maxWidth = 10000, int maxHeight = 10000)
    {
        var width = WindowWidth <= 0 ? 0 : Math.Clamp(WindowWidth, minWidth, maxWidth);
        var height = WindowHeight <= 0 ? 0 : Math.Clamp(WindowHeight, minHeight, maxHeight);

        // 坐标只做粗略约束：负坐标在多屏环境下是合法的（左侧副屏）。
        // 这里用 if 而不是三元表达式：int 与 null 在三元里推不出统一类型。
        int? x = null;
        if (WindowX is { } vx && vx is > -30000 and < 30000)
        {
            x = vx;
        }

        int? y = null;
        if (WindowY is { } vy && vy is > -30000 and < 30000)
        {
            y = vy;
        }

        return this with { WindowWidth = width, WindowHeight = height, WindowX = x, WindowY = y };
    }
}

/// <summary>
/// 设置的读写。
/// </summary>
/// <remarks>
/// <para>
/// <b>损坏时必须回落到默认值而不是抛异常</b>：设置文件是"用起来舒服"的辅助，
/// 不是产品的核心数据。为了它让应用起不来是本末倒置。
/// 但回落也要留下痕迹——静默吞掉会让"我的设置每次都丢"变成无法排查的问题。
/// </para>
/// <para>
/// <b>写入用临时文件加替换</b>：直接覆写在断电或崩溃时会留下半个文件，
/// 下次启动就读出一个"损坏的设置"，于是用户的主题选择莫名其妙地丢了。
/// </para>
/// </remarks>
public static class AppSettingsStore
{
    /// <summary>默认配置文件路径：<c>%LOCALAPPDATA%\EnvStation\settings.json</c>。</summary>
    public static string DefaultPath => Path.Combine(DefaultDirectory, "settings.json");

    /// <summary>默认数据目录：<c>%LOCALAPPDATA%\EnvStation</c>。</summary>
    /// <remarks>
    /// 必须写全 <c>System.Environment</c>：本程序集内有 <c>EnvStation.Core.Environment</c> 命名空间，
    /// 直接写 <c>Environment.SpecialFolder</c> 会被解析到那个命名空间上而报 CS0234。
    /// </remarks>
    public static string DefaultDirectory => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "EnvStation");

    /// <summary>
    /// 读取设置（使用默认路径）。
    /// </summary>
    /// <param name="onRecovered">文件缺失或损坏时的说明（供日志使用）；无异常时为 null。</param>
    /// <returns>设置对象。文件不存在或损坏时返回默认值。</returns>
    public static AppSettings Load(out string? onRecovered) => Load(DefaultPath, out onRecovered);

    /// <summary>
    /// 读取设置。
    /// </summary>
    /// <param name="path">配置文件路径（用 <see cref="DefaultPath"/> 可指定默认位置）。</param>
    /// <param name="onRecovered">文件缺失或损坏时的说明（供日志使用）；无异常时为 null。</param>
    /// <returns>设置对象。文件不存在或损坏时返回默认值。</returns>
    /// <remarks>
    /// 刻意<b>不</b>给 <paramref name="path"/> 默认值：C# 不允许可选参数出现在 <c>out</c> 参数之前，
    /// 而把 <c>out</c> 放到前面又会迫使所有调用点都用位置参数。两个重载读起来更清楚。
    /// </remarks>
    public static AppSettings Load(string path, out string? onRecovered)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        onRecovered = null;
        var target = path;

        string text;
        try
        {
            if (!File.Exists(target))
            {
                return AppSettings.Default;
            }

            text = File.ReadAllText(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onRecovered = $"读取设置失败（{ex.GetType().Name}），已使用默认设置：{target}";
            return AppSettings.Default;
        }

        try
        {
            // 走源生成的 JsonTypeInfo，而不是反射式重载：Core 开了 AOT 基线，
            // 反射式序列化在发布期直接编译失败（IL2026 / IL3050）。
            var parsed = JsonSerializer.Deserialize(text, CoreJsonContext.Default.AppSettings);
            if (parsed is null)
            {
                onRecovered = $"设置文件内容为空，已使用默认设置：{target}";
                return AppSettings.Default;
            }

            if (parsed.SchemaVersion > AppSettings.CurrentSchemaVersion)
            {
                onRecovered =
                    $"设置文件版本（{parsed.SchemaVersion.ToString(CultureInfo.InvariantCulture)}）高于当前程序支持的版本" +
                    $"（{AppSettings.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture)}），已使用默认设置。";
                return AppSettings.Default;
            }

            return parsed.Clamp();
        }
        catch (JsonException ex)
        {
            // 损坏：回落默认值，但把原因带出去。刻意不删除原文件——用户可能想看看里面写了什么。
            onRecovered = $"设置文件无法解析（{ex.Message}），已使用默认设置：{target}";
            return AppSettings.Default;
        }
    }

    /// <summary>
    /// 写入设置。
    /// </summary>
    /// <param name="settings">要保存的设置。</param>
    /// <param name="path">配置文件路径；null 用默认路径。</param>
    /// <returns>成功返回 true；失败返回 false 并把原因写入 <paramref name="error"/>。</returns>
    public static bool Save(AppSettings settings, out string? error, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        error = null;
        var target = path ?? DefaultPath;
        var temp = target + ".tmp";

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(target));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(
                settings with { SchemaVersion = AppSettings.CurrentSchemaVersion },
                CoreJsonContext.Default.AppSettings);

            // 先写临时文件再替换：直接覆写在崩溃/断电时可能留下半个文件，
            // 下次启动读出来就是"损坏的设置"。
            File.WriteAllText(temp, json, System.Text.Encoding.UTF8);
            File.Move(temp, target, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = $"保存设置失败（{ex.GetType().Name}）：{ex.Message}";
            TryDelete(temp);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理临时文件失败不影响主流程：它是 .tmp，下次写入会覆盖。
        }
    }
}
