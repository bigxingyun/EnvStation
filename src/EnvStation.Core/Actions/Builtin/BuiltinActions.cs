using EnvStation.Abstractions.Actions;

namespace EnvStation.Core.Actions.Builtin;

/// <summary>
/// 官方内建动作清单。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是显式列表而不是反射扫描</b>：见 <see cref="ActionRegistry"/> 的说明。
/// 这里再补一条更重要的理由——<b>这份清单就是"包能对用户机器做什么"的完整枚举</b>。
/// 安全评审只需要看这一个方法，就能确认环境站没有提供"任意命令执行""注册表任意写"
/// "创建计划任务""安装服务"这类动作（需求 AI-7）。
/// </para>
/// <para>
/// 分批交付计划（详细设计附录 A）：批次 1 = A01 探测 9 + A05 环境变量 7 + A06 PATH 7 + A09 验证 5。
/// 当前已实现 A01 全组。
/// </para>
/// </remarks>
internal static class BuiltinActions
{
    /// <summary>返回全部官方动作实现。</summary>
    internal static IEnumerable<IAction> All()
    {
        // ── A01 探测与系统信息（9 个 · CAP.INSPECT）──
        yield return new DetectRuntimeAction();
        yield return new DetectArchAction();
        yield return new DetectOsAction();
        yield return new DetectCommandAction();
        yield return new DetectConflictAction();
        yield return new DetectDiskAction();
        yield return new DetectDepsAction();
        yield return new DetectNetworkAction();
        yield return new DetectEnvAction();

        // ── A05 环境变量（7 个 · CAP.ENV.USER / CAP.ENV.MACHINE）──
        yield return new EnvBackupAction();
        yield return new EnvSetAction();
        yield return new EnvUnsetAction();
        yield return new EnvGetAction();
        yield return new EnvDiffAction();
        yield return new EnvRestoreAction();
        yield return new EnvValidateAction();

        // ── A06 PATH 专项（7 个 · CAP.PATH.MODIFY）──
        yield return new PathEnsureAction();
        yield return new PathRemoveAction();
        yield return new PathDedupeAction();
        yield return new PathPrioritizeAction();
        yield return new PathCleanAction();
        yield return new PathValidateAction();
        yield return new PathEnsureShimAction();

        // ── A09 验证与断言（7 个 · CAP.PROCESS.LAUNCH / CAP.INSPECT）──
        yield return new VerifyVersionOutputAction();
        yield return new VerifyCommandResolvesAction();
        yield return new VerifyEnvEffectiveAction();
        yield return new VerifyFileExistsAction();
        yield return new VerifyConflictClearAction();
        yield return new AssertAction();
        yield return new VerifyConfigRestoredAction();

        // ── A10 交互与报告（5 个）──
        yield return new UiNotifyAction();
        yield return new UiPromptAction();
        yield return new UiProgressAction();
        yield return new ReportGenerateAction();
        yield return new ReportExportDiagnosticsAction();

        // ── A11 清理与生命周期（4 个 · CAP.CLEANUP / CAP.FS.INSTALL）──
        yield return new CleanupTempAction();
        yield return new CleanupRemovePackageAction();
        yield return new FileSystemCopyAction();
        yield return new FileSystemMoveAction();

        // ── A02 下载与校验（4 个 · CAP.NET.DOWNLOAD / CAP.INSPECT）──
        yield return new NetDownloadAction();
        yield return new NetHeadAction();
        yield return new NetVerifyHashAction();
        yield return new NetFetchTextAction();

        // ── A03 解压与封装（3 个 · CAP.ARCHIVE / CAP.INSPECT）──
        yield return new ArchiveExtractAction();
        yield return new ArchiveListAction();
        yield return new ArchiveCreateAction();

        // ── A08 配置与镜像源（15 个 · CAP.CONFIG.APP / CAP.INSPECT / CAP.NET.DOWNLOAD）──
        yield return new ConfigDetectAction();
        yield return new ConfigBackupAction();
        yield return new ConfigVerifySyntaxAction();
        yield return new ConfigSetKeyValueAction();
        yield return new MirrorListAction();
        yield return new MirrorTestAction();
        yield return new MirrorSetAction();
        yield return new MirrorRestoreAction();
        yield return new XmlSetMirrorAction();
        yield return new XmlSetRepositoryAction();
        yield return new XmlSetProxyAction();
        yield return new XmlValidateAction();
        yield return new GradleSetMirrorAction();
        yield return new FileWriteTemplateAction();
        yield return new FileMarkedBlockRemoveAction();

        // ── A09 补充：镜像验证（2 个）──
        yield return new VerifyMirrorEffectiveAction();
        yield return new VerifyMirrorIntegrityAction();

        // ── A04 包管理器与安装（6 个 · CAP.PKG.MANAGER / CAP.FS.INSTALL / CAP.LINK / CAP.RUNTIME.MANAGE）──
        yield return new PkgInstallAction();
        yield return new PkgUninstallAction();
        yield return new PkgListAction();
        yield return new LocalInstallAction();
        yield return new LocalRegisterAction();
        yield return new FileSystemLinkAction();

        // ── A07 运行时版本管理（5 个 · CAP.RUNTIME.MANAGE）──
        yield return new RuntimeInstallAction();
        yield return new RuntimeListAction();
        yield return new RuntimeSwitchAction();
        yield return new RuntimeRemoveAction();
        yield return new RuntimeComponentsAction();
    }
}
