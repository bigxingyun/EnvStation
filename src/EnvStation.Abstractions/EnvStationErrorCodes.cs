namespace EnvStation.Abstractions;

/// <summary>
/// 环境站统一错误码。
/// 对应《需求分析.md》12 章"错误码设计"，命名规范见 17.3 节：<c>E_&lt;域&gt;_&lt;原因&gt;</c>。
/// 新增错误码必须同步更新 12 章表格与本地化资源（Strings.resx）。
/// </summary>
public static class EnvStationErrorCodes
{
    /// <summary>成功。统一使用该值可避免"空错误码"在各层之间产生歧义。</summary>
    public const string Ok = "E_OK";

    // ── 权限域 ──
    /// <summary>无权限写入目标作用域（SEC-3 / SEC-5）。</summary>
    public const string EnvScopeDenied = "E_ENV_SCOPE_DENIED";

    /// <summary>用户取消了 UAC 提权。</summary>
    public const string ElevationCancelled = "E_ELEVATION_CANCELLED";

    /// <summary>包未获得某项能力授权，动作被拒绝执行（需求 A2：拒绝而非跳过）。</summary>
    public const string CapabilityDenied = "E_CAPABILITY_DENIED";

    /// <summary>在无人值守模式下遇到需要用户在场的动作。</summary>
    public const string UnattendedInteraction = "E_UNATTENDED_INTERACTION";

    // ── 路径域 ──
    /// <summary>路径含非 ASCII 字符，被规则阻断（需求 6.1 R1）。</summary>
    public const string PathNonAsciiBlocked = "E_PATH_NON_ASCII_BLOCKED";

    /// <summary>目标目录不可写。</summary>
    public const string PathNotWritable = "E_PATH_NOT_WRITABLE";

    /// <summary>路径越出授权根目录（ISO-1 / ISO-2）。</summary>
    public const string PathOutsideAuthorizedRoot = "E_PATH_OUTSIDE_ROOT";

    // ── 前置检查域 ──
    public const string PreflightOsTooOld = "E_PREFLIGHT_OS_TOO_OLD";
    public const string PreflightDiskShort = "E_PREFLIGHT_DISK_SHORT";
    public const string PreflightArchMismatch = "E_PREFLIGHT_ARCH_MISMATCH";

    // ── 网络域 ──
    public const string NetUnreachable = "E_NET_UNREACHABLE";

    /// <summary>哈希校验失败——必须拒绝安装（需求 19.3 AI-4）。</summary>
    public const string NetHashMismatch = "E_NET_HASH_MISMATCH";

    public const string NetSizeLimit = "E_NET_SIZE_LIMIT";

    /// <summary>下载未完成（声明大小与实际不符）。已清理临时文件，可安全重试。</summary>
    public const string NetIncomplete = "E_NET_INCOMPLETE";

    // ── 解压域 ──
    /// <summary>检测到路径穿越，拒绝解压并记录安全事件（ISO-1）。</summary>
    public const string ArchivePathTraversal = "E_ARCHIVE_PATH_TRAVERSAL";

    // ── 配置域 ──
    public const string ConfigXmlParse = "E_CONFIG_XML_PARSE";
    public const string ConfigMergeConflict = "E_CONFIG_MERGE_CONFLICT";
    public const string ConfigVerifyFailed = "E_CONFIG_VERIFY_FAILED";

    // ── 事务域 ──
    /// <summary>快照损坏——禁止后续写操作（NFR-R4）。</summary>
    public const string TxSnapshotInvalid = "E_TX_SNAPSHOT_INVALID";

    /// <summary>回滚失败——提供手动脚本 + 醒目告警（需求 6.4 F5）。</summary>
    public const string TxRollbackFailed = "E_TX_ROLLBACK_FAILED";

    /// <summary>无有效快照时尝试写入（SEC-1）。</summary>
    public const string TxSnapshotRequired = "E_TX_SNAPSHOT_REQUIRED";

    /// <summary>环境变量值超出 Windows 上限。</summary>
    public const string EnvValueTooLong = "E_ENV_VALUE_TOO_LONG";

    /// <summary>环境变量名非法（含 '=' 或为空）。</summary>
    public const string EnvNameInvalid = "E_ENV_NAME_INVALID";

    // ── 并发控制域 ──
    /// <summary>
    /// 等待环境变量锁超时。
    /// 引入原因：M0-P02 用例 P02-10 实测到注册表"读-改-写"会丢失更新，
    /// 因此写入必须在跨进程互斥下进行。
    /// </summary>
    public const string TxMutexTimeout = "E_TX_MUTEX_TIMEOUT";

    /// <summary>无法创建命名互斥体（系统限制或权限问题）。</summary>
    public const string TxMutexUnavailable = "E_TX_MUTEX_UNAVAILABLE";

    // ── 动作域 ──
    /// <summary>动作 ID 未在注册表中登记。</summary>
    public const string ActionNotFound = "E_ACTION_NOT_FOUND";

    /// <summary>动作版本或实现哈希与包内固定值不符（需求 S3）。</summary>
    public const string ActionPinMismatch = "E_ACTION_PIN_MISMATCH";

    /// <summary>动作参数未通过 schema 校验（需求 AC-1）。</summary>
    public const string ActionArgumentInvalid = "E_ACTION_ARGUMENT_INVALID";

    /// <summary>动作执行超时（需求 AC-5）。</summary>
    public const string ActionTimeout = "E_ACTION_TIMEOUT";

    /// <summary>动作内部失败，未归入更具体错误码。</summary>
    public const string ActionFailed = "E_ACTION_FAILED";

    /// <summary>断言失败（<c>envstation.assert</c>）。</summary>
    public const string AssertFailed = "E_ASSERT_FAILED";

    // ── 包与脚本域 ──
    /// <summary>包清单或工作流解析失败。</summary>
    public const string PackageParseFailed = "E_PACKAGE_PARSE_FAILED";

    /// <summary>包清单缺少必需字段。</summary>
    public const string PackageManifestInvalid = "E_PACKAGE_MANIFEST_INVALID";

    /// <summary>包使用了非法的动作命名空间（需求 S1）。</summary>
    public const string PackageNamespaceViolation = "E_PACKAGE_NAMESPACE_VIOLATION";

    /// <summary>包声明了未知能力。</summary>
    public const string PackageUnknownCapability = "E_PACKAGE_UNKNOWN_CAPABILITY";

    /// <summary>包版本与当前客户端 <c>spec_version</c> 不兼容（需求 M13-7）。</summary>
    public const string PackageSpecIncompatible = "E_PACKAGE_SPEC_INCOMPATIBLE";

    /// <summary>静态检查未通过，禁止打包或导入（需求 M13-3）。</summary>
    public const string PackageStaticCheckFailed = "E_PACKAGE_STATIC_CHECK_FAILED";

    // ── 工作流域 ──
    /// <summary>工作流引用了不存在的变量。</summary>
    public const string WorkflowVariableUndefined = "E_WORKFLOW_VARIABLE_UNDEFINED";

    /// <summary>表达式求值失败或超时（需求 20.6）。</summary>
    public const string WorkflowExpressionFailed = "E_WORKFLOW_EXPRESSION_FAILED";

    /// <summary>循环次数超过硬上限（需求 20.5）。</summary>
    public const string WorkflowLoopLimitExceeded = "E_WORKFLOW_LOOP_LIMIT_EXCEEDED";

    /// <summary>步骤引用图存在环或嵌套超过上限。</summary>
    public const string WorkflowCycleDetected = "E_WORKFLOW_CYCLE_DETECTED";

    /// <summary>工作流执行被 <c>on_error: rollback</c> 中止并已回滚。</summary>
    public const string WorkflowAborted = "E_WORKFLOW_ABORTED";

    // ── 规则引擎域 ──
    /// <summary>被 block 级规则阻断（需求 24.2）。</summary>
    public const string RuleBlocked = "E_RULE_BLOCKED";

    /// <summary>规则求值超时，按安全侧处理（跳过并告警）。</summary>
    public const string RuleEvaluationTimeout = "E_RULE_EVAL_TIMEOUT";

    // ── 校验域（V1~V4）──
    /// <summary>签名缺失或验证失败（需求 24.1 V1）。</summary>
    public const string VerifySignatureFailed = "E_VERIFY_SIGNATURE_FAILED";

    /// <summary>内容哈希与清单声明不符。</summary>
    public const string VerifyHashMismatch = "E_VERIFY_HASH_MISMATCH";

    /// <summary>沙箱试运行未通过（需求 24.1 V4）。</summary>
    public const string VerifySandboxFailed = "E_VERIFY_SANDBOX_FAILED";
}
