using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EnvStation.App.Mvvm;

/// <summary>
/// 视图模型基类：只提供 <see cref="INotifyPropertyChanged"/> 的最小实现。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不引入 CommunityToolkit.Mvvm</b>：本仓库的纪律是 <c>src</c> 内除 WinUI / SDK 工具外零 NuGet 依赖
/// （见 M16-9）。一个 <c>INotifyPropertyChanged</c> 的实现只有十几行，不值得为它开一个依赖口子——
/// 一旦开了，"再加一个包"的成本就变得很低。
/// </para>
/// <para>
/// 源生成器（<c>[ObservableProperty]</c>）的便利性确实没有替代品，但那属于"写起来省事"，
/// 不属于"做不到"。本项目的属性数量在几十个量级，手写完全可承受。
/// </para>
/// </remarks>
internal abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>触发属性变更通知。</summary>
    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>赋值并在值确实变化时触发通知；返回是否发生了变化。</summary>
    /// <remarks>
    /// 相等即不通知：WinUI 的绑定在收到通知后会重新求值，
    /// 无差别通知会让"每帧都在重画"这种问题变得难以定位。
    /// </remarks>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(propertyName);
        return true;
    }
}
