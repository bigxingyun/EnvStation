using System.Text.Json.Serialization;

namespace EnvStation.Core.Configuration;

/// <summary>
/// Core 层持久化类型的 JSON 源生成上下文。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须用源生成而不是反射式序列化</b>：本工程开了 AOT 基线（<c>EnableAotBaseline=true</c>），
/// 反射式 <c>JsonSerializer.Serialize/Deserialize</c> 会被裁掉或运行时报错。
/// 这不是"最佳实践建议"，是**构建期的硬错误**——<c>IL2026</c> / <c>IL3050</c> 直接让发布失败。
/// 第一版 <see cref="AppSettingsStore"/> 就是反射式的，`verify-m0.ps1` 的 AOT 发布步骤当场拦下。
/// </para>
/// <para>
/// <b>为什么不在 <c>EnvStationJsonContext</c> 里登记</b>：那个上下文在 <c>EnvStation.Abstractions</c>，
/// 而 <c>AppSettings</c> 在 <c>EnvStation.Core</c>。让抽象层引用 Core 会造成反向依赖（违反 DL-1/DL-2），
/// 因此 Core 自己持有一个上下文。
/// </para>
/// <para>
/// <b><c>PropertyNameCaseInsensitive</c> 不能省</b>：源生成默认**不**忽略大小写，
/// 而反射式路径默认忽略——换成源生成之后，手工把属性名写成 <c>"Theme"</c> 的配置文件会突然读不出来。
/// 设置文件恰恰是"用户可能打开看一眼甚至改一下"的东西，属性名大小写不该成为它失效的理由。
/// 这条是被 CF-43 抓出来的：用例单独跑通过、在套件里失败，
/// 根因正是"写的路径产出 camelCase、而用例手写的 JSON 是 PascalCase"。
/// </para>
/// <para>
/// <b>选项口径与抽象层保持一致</b>：缩进、驼峰命名、枚举写成字符串——配置文件是给人看的，
/// 写成数字（<c>"Theme": 1</c>）会让人无法手工修正。
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
public sealed partial class CoreJsonContext : JsonSerializerContext;
