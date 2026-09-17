# 环境站示例包

这个包只做两件事：检测 Python，并检查 PATH。

它申请了两个能力：
- `CAP.INSPECT`：只读探测
- `CAP.PATH.MODIFY`：在你确认后修改用户级 PATH

安装：
```
envstation check python-env.envstation
envstation run python-env.envstation --apply --allow CAP.INSPECT --allow CAP.PATH.MODIFY --root "%LOCALAPPDATA%\EnvStation"
```
