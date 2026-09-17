using System.Runtime.Versioning;

// 环境站是 Windows 专属产品；在程序集级声明一次，避免每处 Registry / Windows API 调用都报 CA1416。
// 理由与 Core/AssemblyInfo.cs 相同。
[assembly: SupportedOSPlatform("windows")]