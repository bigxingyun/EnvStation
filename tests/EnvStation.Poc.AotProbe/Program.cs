using System.Security.Cryptography;
using System.Text;

namespace EnvStation.Poc.AotProbe;

/// <summary>
/// M0-P00 · AOT 兼容性三连测。
///
/// <para><b>为什么这是第一天必做项：</b>这三个库分别决定<b>组件定义格式</b>（TOML vs JSON）、
/// <b>包签名方案</b>（Ed25519 / ECDSA / sidecar）、<b>解压能力</b>（7z/tar.gz vs 仅 zip）。
/// 若在写完大量代码后才发现不可用，返工成本极高。</para>
///
/// <para><b>判定标准</b>（见任务书 P00 卡片）：</para>
/// <list type="bullet">
///   <item>能以 <c>PublishAot=true + PublishTrimmed=true</c> 成功发布；</item>
///   <item>发布过程无致命裁剪警告（IL2026 / IL3050 等）；</item>
///   <item>AOT 产物<b>运行时行为正确</b>（往返序列化、解压、签名验证结果与 JIT 一致）。</item>
/// </list>
///
/// <para>本工程同时接受 <c>--jit</c> 参数以便与 JIT 结果对照——两者不一致即说明存在裁剪/反射问题。</para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var isAot = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
        Console.WriteLine("M0-P00 · AOT 兼容性三连测");
        Console.WriteLine($"运行模式：{(isAot ? "Native AOT（本机代码）" : "JIT（可动态生成代码）")}");
        Console.WriteLine($"运行时：{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine();

        var h = new TestKit.TestHarness("M0-P00 AOT 兼容性");

        ProbeToml(h);
        ProbeCrypto(h);
        ProbeArchive(h);
        ProbeJsonSourceGen(h);

        var jsonPath = TestKit.TestHarness.GetJsonPath(args);
        if (jsonPath is not null)
        {
            h.WriteJson(jsonPath);
        }

        var code = h.Summarize();
        Console.WriteLine();
        Console.WriteLine("提示：本工程必须分别在 JIT 与 AOT 两种模式下运行，并比对结论。");
        return code;
    }

    // ────────────────────────── P00-a · Tomlyn ──────────────────────────

    private static void ProbeToml(TestKit.TestHarness h)
    {
#if HAS_TOMLYN
        h.Case("P00-a1", "Tomlyn：解析含嵌套表/数组/注释的 TOML", () =>
        {
            const string toml = """
                # 组件定义示例（含注释，必须被正确忽略）
                spec_version = "1.0"
                id = "java.jdk"
                display_name = "Java SE JDK"

                [detect]
                command = "java"
                args = ["-version"]

                [[detect.dirs]]
                path = "C:\\Program Files\\Java"
                marker = "bin/java.exe"

                [[detect.dirs]]
                path = "%ProgramFiles%\\Eclipse Adoptium"
                marker = "bin/java.exe"
                """;

            var model = Tomlyn.Toml.ToModel(toml);

            Assert.True(model.ContainsKey("spec_version"), "应解析出顶层键");
            Assert.Equal("java.jdk", model["id"]?.ToString(), "字符串值应正确");

            var detect = model["detect"] as Tomlyn.Model.TomlTable;
            Assert.NotNull(detect, "嵌套表应被解析为 TomlTable");
            Assert.Equal("java", detect!["command"]?.ToString(), "嵌套表内取值应正确");

            var dirs = detect["dirs"] as Tomlyn.Model.TomlTableArray;
            Assert.NotNull(dirs, "数组表应被解析为 TomlTableArray");
            Assert.Equal(2, dirs!.Count, "数组表应有 2 项");
            Assert.Equal(@"C:\Program Files\Java", dirs[0]["path"]?.ToString(), "首项路径应正确");

            var argsArr = detect["args"] as Tomlyn.Model.TomlArray;
            Assert.NotNull(argsArr, "数组应被解析为 TomlArray");
            Assert.Equal(1, argsArr!.Count, "args 应有 1 项");
        });

        h.Case("P00-a2", "Tomlyn：模型往返序列化一致", () =>
        {
            const string toml = """
                id = "python"
                version = "3.12.4"

                [env]
                mode = "pathOnly"
                entries = ["{home}", "{home}\\Scripts"]
                """;

            var model = Tomlyn.Toml.ToModel(toml);
            var roundTrip = Tomlyn.Toml.FromModel(model);
            var reparsed = Tomlyn.Toml.ToModel(roundTrip);

            Assert.Equal(model["id"]?.ToString(), reparsed["id"]?.ToString(), "往返后顶层键应一致");

            var env1 = model["env"] as Tomlyn.Model.TomlTable;
            var env2 = reparsed["env"] as Tomlyn.Model.TomlTable;
            Assert.Equal(env1!["mode"]?.ToString(), env2!["mode"]?.ToString(), "嵌套值应一致");

            var e1 = env1["entries"] as Tomlyn.Model.TomlArray;
            var e2 = env2["entries"] as Tomlyn.Model.TomlArray;
            Assert.Equal(e1!.Count, e2!.Count, "数组长度应一致");
            Assert.Equal(e1[1]?.ToString(), e2[1]?.ToString(), "数组元素应一致");
        });

        h.Case("P00-a3", "Tomlyn：非法 TOML 抛出可识别异常（而非崩溃）", () =>
        {
            Assert.Throws<Tomlyn.TomlException>(
                () => Tomlyn.Toml.ToModel("id = \"unterminated"),
                "语法错误应抛出 TomlException，便于我们转换为 E_CONFIG_* 错误码");
        });
#else
        h.Case("P00-a1", "Tomlyn：未参与本次验证", () =>
            Assert.Skip("构建时以 -p:WithTomlyn=false 关闭；结论为『未验证』，不得视为通过"));
        h.Case("P00-a2", "Tomlyn：未参与本次验证", () => Assert.Skip("同上"));
        h.Case("P00-a3", "Tomlyn：未参与本次验证", () => Assert.Skip("同上"));
#endif
    }

    // ────────────────────────── P00-b · 签名算法 ──────────────────────────

    private static void ProbeCrypto(TestKit.TestHarness h)
    {
        // 优先探测 .NET 原生 Ed25519 支持（.NET 8 的 System.Security.Cryptography 可能尚未包含）
        ProbeNativeEd25519(h);
        ProbeEcdsaFallback(h);

#if HAS_NSEC
        h.Case("P00-b3", "NSec：Ed25519 签名与验签全流程", () =>
        {
            var algorithm = NSec.Cryptography.SignatureAlgorithm.Ed25519;
            var creationParameters = new NSec.Cryptography.KeyCreationParameters
            {
                ExportPolicy = NSec.Cryptography.KeyExportPolicies.AllowPlaintextExport,
            };

            using var key = NSec.Cryptography.Key.Create(algorithm, creationParameters);
            var pub = key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);
            var priv = key.Export(NSec.Cryptography.KeyBlobFormat.RawPrivateKey);

            var data = Encoding.UTF8.GetBytes("package manifest v1");
            var signature = algorithm.Sign(key, data);

            Assert.Equal(64, signature.Length, "Ed25519 签名应为 64 字节");

            using var imported = NSec.Cryptography.Key.Import(
                algorithm, priv, creationParameters);
            Assert.True(
                algorithm.Verify(imported.PublicKey, data, signature),
                "合法签名应验证通过");

            var tampered = (byte[])data.Clone();
            tampered[0] ^= 0xFF;
            Assert.False(
                algorithm.Verify(imported.PublicKey, tampered, signature),
                "被篡改的数据必须验签失败（这是包完整性的根本保证）");

            Console.WriteLine($"         公钥长度 {pub.Length} 字节，私钥 {priv.Length} 字节，签名 {signature.Length} 字节");
        });
#else
        h.Case("P00-b3", "NSec：未参与本次验证", () =>
            Assert.Skip("构建时未开启 -p:WithNSec=true"));
#endif
    }

    private static void ProbeNativeEd25519(TestKit.TestHarness h)
    {
        h.Case("P00-b1", "探测 .NET 原生 Ed25519 支持", () =>
        {
            var type = Type.GetType("System.Security.Cryptography.Ed25519, System.Security.Cryptography");
            if (type is null)
            {
                Console.WriteLine("         结论：.NET 8 未提供 System.Security.Cryptography.Ed25519");
                Assert.Skip("BCL 无原生 Ed25519 —— 需改用 NSec、ECDSA 或 sidecar（见备选路径）");
            }

            Console.WriteLine($"         结论：发现原生类型 {type!.FullName}");
            Assert.True(true, "存在原生类型，后续可优先使用");
        });
    }

    private static void ProbeEcdsaFallback(TestKit.TestHarness h)
    {
        h.Case("P00-b2", "备选路径：ECDSA P-256 签名/验签（BCL 原生，AOT 友好）", () =>
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var data = Encoding.UTF8.GetBytes("package manifest v1");
            var signature = ecdsa.SignData(data, HashAlgorithmName.SHA256);
            var publicKey = ecdsa.ExportSubjectPublicKeyInfo();

            Assert.True(
                ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256),
                "ECDSA 合法签名应验证通过");

            var tampered = (byte[])data.Clone();
            tampered[^1] ^= 0x01;
            Assert.False(
                ecdsa.VerifyData(tampered, signature, HashAlgorithmName.SHA256),
                "被篡改数据必须验签失败");

            // 跨实例验证：签名可被独立实例用导出的公钥验证（模拟"用户核对发布者指纹"）
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKey, out _);
            Assert.True(
                verifier.VerifyData(data, signature, HashAlgorithmName.SHA256),
                "独立实例应能用导出公钥验证签名");

            Console.WriteLine($"         P-256 签名 {signature.Length} 字节，公钥 {publicKey.Length} 字节（ASN.1）");
        });

        h.Case("P00-b4", "SHA-256 内容哈希（快照与包校验的公共基础）", () =>
        {
            // 用广为人知的测试向量校验实现正确性，避免"自证"
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("abc"));

            // 注意：Convert.ToHexStringLower 是 .NET 9 新增 API，本项目固定 net8.0，
            // 因此统一用 Convert.ToHexString(...).ToLowerInvariant()。
            var hex = Convert.ToHexString(hash).ToLowerInvariant();

            Assert.Equal(
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                hex,
                "SHA-256(\"abc\") 应等于标准测试向量");
            Assert.Equal(64, hex.Length, "SHA-256 十六进制应为 64 字符");
            Assert.Equal(hex, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("abc"))).ToLowerInvariant(),
                "哈希应可复现（同输入同输出）");
        });
    }

    // ────────────────────────── P00-c · 解压 ──────────────────────────

    private static void ProbeArchive(TestKit.TestHarness h)
    {
        h.Case("P00-c1", "BCL 原生 zip：创建 → 解压 → 校验内容", () =>
        {
            var work = Path.Combine(Path.GetTempPath(), "envstation-p00-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);

            try
            {
                var zipPath = Path.Combine(work, "test.zip");
                using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
                {
                    var entry = zip.CreateEntry("jdk-17/bin/java.exe");
                    using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                    writer.Write("fake-java-binary");
                }

                var extractDir = Path.Combine(work, "out");
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir);

                var extracted = Path.Combine(extractDir, "jdk-17", "bin", "java.exe");
                Assert.True(File.Exists(extracted), "应正确解压出嵌套路径文件");
                Assert.Equal("fake-java-binary", File.ReadAllText(extracted), "内容应一致");
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch (IOException) { /* 忽略 */ }
            }
        });

#if HAS_SHARPCOMPRESS
        h.Case("P00-c2", "SharpCompress：解压 zip（含嵌套目录）", () =>
        {
            var work = Path.Combine(Path.GetTempPath(), "envstation-p00-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);

            try
            {
                // 先用 BCL 造一个 zip，再用 SharpCompress 读——顺带验证跨库兼容
                var zipPath = Path.Combine(work, "test.zip");
                using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
                {
                    for (var i = 0; i < 5; i++)
                    {
                        var entry = zip.CreateEntry($"lib/mod{i % 2}/file{i}.bin");
                        using var writer = new StreamWriter(entry.Open());
                        writer.Write($"payload-{i}");
                    }
                }

                var extractDir = Path.Combine(work, "out");
                using var archive = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(zipPath, null);
                var count = 0;
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    var target = Path.Combine(extractDir, entry.Key!);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                    // SharpCompress 0.48 起 ZipArchive.Open 改名为 OpenArchive（0.39~0.47 存在 zip-slip 漏洞，见 GHSA-6c8g-7p36-r338）；
                    // 官方推荐改用 IArchiveEntry.OpenEntryStream()，由调用方决定落盘方式
                    // （这恰好也是我们需要的：解压动作要在写盘前做路径穿越与体积比检查）。
                    using var source = entry.OpenEntryStream();
                    using var destination = File.Create(target);
                    source.CopyTo(destination);
                    count++;
                }

                Assert.Equal(5, count, "应解压出 5 个文件");
                Assert.True(File.Exists(Path.Combine(extractDir, "lib", "mod1", "file3.bin")), "嵌套路径应正确");
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch (IOException) { /* 忽略 */ }
            }
        });

        h.Case("P00-c3", "SharpCompress：路径穿越防护（安全关键）", () =>
        {
            // 构造一个含 ../ 的恶意条目，验证我们能否在写入前识别
            var work = Path.Combine(Path.GetTempPath(), "envstation-p00-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);

            try
            {
                var zipPath = Path.Combine(work, "evil.zip");
                using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
                {
                    var entry = zip.CreateEntry("../../escaped.txt");
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write("should-never-be-written");
                }

                using var archive = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(zipPath, null);
                var dangerous = archive.Entries
                    .Where(e => !e.IsDirectory)
                    .Select(e => e.Key ?? string.Empty)
                    .Where(key => key.Contains("..", StringComparison.Ordinal))
                    .ToList();

                Assert.True(dangerous.Count > 0,
                    "必须能识别出含 ../ 的条目 —— 这是 ISO-1 路径穿越防护的判定依据");

                Console.WriteLine($"         识别到危险条目：{string.Join(", ", dangerous)}");
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch (IOException) { /* 忽略 */ }
            }
        });
#else
        h.Case("P00-c2", "SharpCompress：未参与本次验证", () =>
            Assert.Skip("构建时以 -p:WithSharpCompress=false 关闭；解压能力需退化为仅 zip"));
        h.Case("P00-c3", "SharpCompress：未参与本次验证", () => Assert.Skip("同上"));
#endif
    }

    // ────────────────────────── 附：源生成 JSON（AOT 关键路径） ──────────────────────────

    private static void ProbeJsonSourceGen(TestKit.TestHarness h)
    {
        h.Case("P00-d1", "源生成 JSON：快照模型往返（AOT 关键路径）", () =>
        {
            var snapshot = new EnvStation.Abstractions.Environment.EnvironmentSnapshot
            {
                SchemaVersion = "1.0",
                SnapshotId = "20260914-120000-auto-test-abcd",
                CreatedAt = DateTimeOffset.Parse("2026-09-14T12:00:00+08:00"),
                Trigger = "unit-test",
                UserVariables =
                [
                    new EnvStation.Abstractions.Environment.EnvVariable
                    {
                        Name = "JAVA_HOME",
                        RawValue = @"D:\Dev\jdk-17.0.9",
                        Kind = EnvStation.Abstractions.Environment.EnvValueKind.String,
                    },
                ],
                MachineVariables = [],
                ContentHash = null,
            };

            var json = System.Text.Json.JsonSerializer.Serialize(
                snapshot, EnvStation.Abstractions.Serialization.EnvStationJsonContext.Default.EnvironmentSnapshot);

            Assert.Contains("\"schemaVersion\"", json, "应使用 camelCase 命名策略（源生成配置生效）");
            Assert.Contains("\"JAVA_HOME\"", json, "变量名应被序列化");

            var back = System.Text.Json.JsonSerializer.Deserialize(
                json, EnvStation.Abstractions.Serialization.EnvStationJsonContext.Default.EnvironmentSnapshot);

            Assert.NotNull(back, "应能反序列化");
            Assert.Equal(snapshot.SnapshotId, back!.SnapshotId, "往返后 ID 应一致");
            Assert.Equal("JAVA_HOME", back.UserVariables[0].Name, "变量应完整往返");
            Assert.Equal(
                EnvStation.Abstractions.Environment.EnvValueKind.String,
                back.UserVariables[0].Kind,
                "枚举应以字符串形式往返（UseStringEnumConverter）");
        });

        h.Case("P00-d2", "当前运行模式确认（AOT / JIT）", () =>
        {
            var dynamicCode = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
            Console.WriteLine($"         IsDynamicCodeSupported = {dynamicCode} → {(dynamicCode ? "JIT" : "AOT")}");

            // 本用例只做记录：同一套断言必须在两种模式下都通过，才算 AOT 兼容
            Assert.True(true, "记录运行模式");
        });
    }
}
