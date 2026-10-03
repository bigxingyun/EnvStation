# python-install 示例

用本机 winget 安装 `Python.Python.3.12`，再检测是否可用。

```powershell
dotnet run --project src\EnvStation.Cli -c Release -- pack .\samples\python-install --out .\python-install.envstation
dotnet run --project src\EnvStation.Cli -c Release -- check .\python-install.envstation --v4
dotnet run --project src\EnvStation.Cli -c Release -- run .\python-install.envstation

# 确认后真正安装：
dotnet run --project src\EnvStation.Cli -c Release -- run .\python-install.envstation `
  --apply --allow CAP.PKG.MANAGER --allow CAP.INSPECT `
  --root "$env:LOCALAPPDATA\EnvStation"
```

图形界面：导入包 → 校验 → 勾选能力 → 预演或执行。

写包步骤见仓库根目录 README 的「如何编写包」。
