# python-env 示例

检测本机有没有 Python，并检查用户 PATH。

申请的能力：

- `CAP.INSPECT`：只读探测
- `CAP.PATH.MODIFY`：在你确认后改用户 PATH（本示例工作流未必会写）

```powershell
dotnet run --project src\EnvStation.Cli -c Release -- pack .\samples\python-env --out .\python-env.envstation
dotnet run --project src\EnvStation.Cli -c Release -- check .\python-env.envstation --v4
dotnet run --project src\EnvStation.Cli -c Release -- run .\python-env.envstation
```

写包步骤见仓库根目录 README 的「如何编写包」。
