// ============================================================================
//  测试工程共享的全局 using
//  （由 tests/Directory.Build.props 通过 <Compile Include> 链接进各测试工程）
//
//  为什么需要这里：测试工程命名空间形如 EnvStation.Tests.Transactions，而 EnvStation
//  命名树中同时存在 EnvStation.TestKit / EnvStation.Core.*。当代码里直接写 TestHarness、
//  Assert、IEnvironmentStore、Result<> 时，编译器会先向上遍历外层命名空间，在 EnvStation
//  处找到 "Tests" / "Core" 子命名空间后即停止在全局命名空间搜索，于是报 CS0246。
//
//  两类声明各司其职：
//    · global using <命名空间>;        —— 让该命名空间内的**类型名**可直接使用
//                                        （如 Result<>、Unit、Assert、TestHarness）
//    · global using 别名 = <命名空间>; —— 供**显式限定**时使用，避免与被遮蔽的名字相撞
// ============================================================================

// ① 类型可直接使用（解决 CS0246 的关键）
global using EnvStation.Abstractions;
global using EnvStation.TestKit;

// ② 显式限定用的命名空间别名
global using Abs = EnvStation.Abstractions;
global using AbsEnv = EnvStation.Abstractions.Environment;
global using AbsTx = EnvStation.Abstractions.Transactions;
global using AbsPkg = EnvStation.Abstractions.Packages;
global using AbsSer = EnvStation.Abstractions.Serialization;
global using AbsDiag = EnvStation.Abstractions.Diagnostics;
global using AbsActions = EnvStation.Abstractions.Actions;
global using CoreEnv = EnvStation.Core.Environment;
global using CoreTx = EnvStation.Core.Transactions;
global using CoreInstall = EnvStation.Core.Installation;
global using CoreActions = EnvStation.Core.Actions;
global using CoreToml = EnvStation.Core.Toml;
global using CorePkg = EnvStation.Core.Packages;
global using CoreScript = EnvStation.Core.Scripting;
global using CoreWf = EnvStation.Core.Workflow;

// ③ 注册表别名。
//    必须用别名而不是直接 using Microsoft.Win32 —— 因为测试工程命名空间形如
//    EnvStation.Tests.RegistrySandbox，会让简单名 "Registry" 解析到命名空间而非类型，
//    导致 "命名空间 xxx 中不存在类型或命名空间名 CurrentUser" 这类费解的报错。
global using Reg = Microsoft.Win32;
