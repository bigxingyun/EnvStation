using EnvStation.App.Controls;
using EnvStation.Core.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EnvStation.App;

/// <summary>
/// 「设置」页：真正可改的设置，并跨次启动保留。
/// </summary>
/// <remarks>
/// <para>
/// <b>它替换掉的是什么</b>：原设置页是九行「标签 + 值」的静态文字，**没有一件可改的东西**。
/// 其中那行「深色 / 浅色，右上角切换」是一句**指路说明**——它指向窗口别处的按钮，
/// 而不是一个控件；而且那个按钮只能在深浅之间对翻，"跟随系统"根本选不了。
/// 更根本的是：整个工程没有任何设置持久化，主题与窗口尺寸重启即丢。
/// </para>
/// <para>
/// <b>表单宽度</b>：规范 3.1 节要求表单最大 720px 居中——表单太宽时眼睛要横扫整个屏幕才能
/// 从标签找到值。列表与表格铺满，表单收窄，这是两种不同的阅读行为。
/// </para>
/// </remarks>
internal sealed partial class MainWindow
{
    /// <summary>表单最大宽度（规范 3.1 节）。</summary>
    private const double FormMaxWidth = 720;

    /// <summary>构建「设置」页。</summary>
    private UIElement BuildSettingsPage()
    {
        var form = UiKit.Stack(DesignTokens.RhythmBetweenGroups);
        form.MaxWidth = FormMaxWidth;
        form.HorizontalAlignment = HorizontalAlignment.Left;

        form.Children.Add(UiKit.Title("设置"));

        form.Children.Add(BuildAppearanceCard());
        form.Children.Add(BuildSafetyCard());
        form.Children.Add(BuildAboutCard());

        return UiKit.Scroll(form);
    }

    /// <summary>外观：主题（三态）与密度、字体（只读事实）。</summary>
    private UIElement BuildAppearanceCard()
    {
        var (card, body) = UiKit.CardWithBody(DesignTokens.RhythmBetweenGroups);

        body.Children.Add(UiKit.SectionLabel("外观"));
        body.Children.Add(BuildThemeSelector());

        body.Children.Add(UiKit.Row(
            "界面密度",
            UiKit.Body($"桌面档 · 列表项 {DesignTokens.ListItemHeight:0}px · 按钮 {DesignTokens.ButtonHeight:0}px")));

        body.Children.Add(UiKit.Row("字体", UiKit.Body("标准字号")));

        return card;
    }

    /// <summary>
    /// 主题选择器：三态。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须是三态而不是一个开关</b>：规范 TH-2 要求「跟随系统 / 深色 / 浅色」。
    /// 上一版只有深浅对翻，而且因为初始值是"跟随系统"、取反逻辑把它当成了浅色，
    /// **首次点击必然跳到深色**——用户想跟随系统时没有任何办法。
    /// </remarks>
    private UIElement BuildThemeSelector()
    {
        var combo = new ComboBox
        {
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        combo.Items.Add("跟随系统");
        combo.Items.Add("深色");
        combo.Items.Add("浅色");
        combo.SelectedIndex = (int)CurrentSettings.Theme;

        combo.SelectionChanged += (_, _) =>
        {
            var choice = (ThemeChoice)Math.Clamp(combo.SelectedIndex, 0, 2);
            if (choice == CurrentSettings.Theme)
            {
                return;
            }

            ApplyThemeChoice(choice);
        };

        var panel = UiKit.Stack(DesignTokens.RhythmInGroup);
        panel.Children.Add(combo);
        panel.Children.Add(UiKit.Body(
            CurrentSettings.Theme == ThemeChoice.System
                ? "当前跟随系统的应用模式。"
                : "当前使用固定的主题。",
            secondary: true));

        return UiKit.Row("主题", panel);
    }

    /// <summary>安全与还原：只读事实。这些是引擎行为，不是可选项——写成开关会误导用户。</summary>
    private static UIElement BuildSafetyCard()
    {
        var (card, body) = UiKit.CardWithBody(DesignTokens.RhythmInGroup);

        body.Children.Add(UiKit.SectionLabel("安全与还原"));
        body.Children.Add(UiKit.Row("环境变量快照", UiKit.Body("写入前自动创建")));
        body.Children.Add(UiKit.Row("配置文件备份", UiKit.Body("写入前备份，校验失败自动还原")));
        body.Children.Add(UiKit.Row("权限", UiKit.Body("标准用户运行，系统级操作按需提权")));

        return card;
    }

    private UIElement BuildAboutCard()
    {
        var (card, body) = UiKit.CardWithBody(DesignTokens.RhythmInGroup);

        body.Children.Add(UiKit.SectionLabel("关于"));
        body.Children.Add(UiKit.Row("版本", UiKit.Mono("0.1.0")));
        body.Children.Add(UiKit.Row("动作库", UiKit.Body($"{_kernel.ActionCount} 个动作")));

        var dataDir = UiKit.Stack(DesignTokens.RhythmInGroup);
        dataDir.Children.Add(UiKit.Mono(AppSettingsStore.DefaultDirectory, "MonoSmall"));
        dataDir.Children.Add(UiKit.ButtonBar(
            AppControls.CopyButton(AppSettingsStore.DefaultDirectory, path =>
                SetStatus($"已复制数据目录路径", "settings"))));
        body.Children.Add(UiKit.Row("数据目录", dataDir));

        body.Children.Add(UiKit.Row("设置文件", UiKit.Mono(AppSettingsStore.DefaultPath, "MonoSmall")));

        return card;
    }
}
