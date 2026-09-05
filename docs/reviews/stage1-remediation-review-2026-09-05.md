# 阶段一审批修复闭环审查

日期：2026-09-05  
固定点：`54a30e310e28c8135d2814a204a1e2a73753df53`  
审查提交：`b42068c5dafb5ad604974dee398f0b8ad470fcba`  
规范源：`ContainerBenchmark_Plan.md` §2/§3.4/§4、`ContainerBenchmark_ExecutionSplit.md` 阶段 1、独立项目 README Evidence contract

## 结论

阶段一批准通过。原审批遗留均已修复：119 个用例全部通过且 0 skip；Job 单值结果恢复为单元素 `NativeArray`；NativeQueue D4 使用 `AsReadOnly().GetEnumerator()` 零拷贝纳入主矩阵；NativeQueue 容量/存储语义进入每条 Native 结果；Collections 基线纠正为 Unity 6000.5 固定的 6.5.0 Core package。

生命周期修复没有改变计时操作语义：普通/可变用例仍逐 pass 建销；29 个显式只读用例只复用 fixture，每 pass 的 Checksum/Job 结果复位仍在 Stopwatch 外，Run/Validate 仍执行 11 次。异常路径同时覆盖 Run 异常、部分 Setup 失败、Teardown 失败及执行+释放双异常。

阶段二尚未在本文件审批；它必须等待双 IL2CPP 正式矩阵、离线 HTML 和分析文档完成。

## 验证证据

- Unity：`6000.5.9f1 (b57deb96f08d)`；Collections：`6.5.0 / builtin`；packages-lock SHA-256：`5496DF13106EE5FE221DD76C28BE21048FA43163AF6C657FF034EE7FE2B779E6`。
- 阶段一自测：`Logs/stage1-selftest-119-auditfix.log`，`119 passed / 0 failed / 0 skipped`，退出码 0，编译错误 0，Native/TempJob 生命周期警告 0。
- 生产生命周期：`Logs/setup-cost-matrix-auditfix.log`，`29/29`；正常计数 `1/11/11/11/1`；故障释放与双异常保护均 passed。
- 5K 全碰撞 C5 诊断：修复前墙钟 2462.29ms；修复后最新回归 218.76ms，约 11.25 倍墙钟改善。这里仅证明消除了重复 Setup，不作为容器性能结论。
- 静态门：无旧 `2.5.7`、无 Job 单值 `NativeList`、29 个复用类中无 TempJob、四个 NativeQueue 类均有 `SemanticNote`、`git diff --check` 通过。

## Standards

硬违约 0。逐 hunk 对照 README Evidence contract、主计划 §2/§4 与主工程 `AGENTS.md` 性能审查原则，生命周期、计时区及环境证据均符合。

Judgement call 1：`ContainerBenchmarkStandaloneBuild.cs` 最初用 `manifestText.Contains(expectedManifestEntry)` 校验依赖，属于可能的 Primitive Obsession，合法空白调整会误拒构建。

## Spec

0 findings。缺失/部分实现 0；scope creep 0；伪实现/错误实现 0。修复覆盖环境锁定、Job 公平性与 `NativeArray` 结果、D4/容量语义及只读 fixture 生命周期。尚待执行的双 IL2CPP 正式矩阵不计为本阶段一修复提交缺陷。

## Standards finding 处理

该判断项已在审批落盘前修复：构建入口改用 `System.Text.Json` 读取 manifest/lock 的字段，并用 `PackageInfo.FindForPackageName` 核验直接依赖、实际版本和 `PackageSource.BuiltIn`；完整 Editor revision 与 lock SHA 校验保持不变。

汇总：Standards 0 个硬违约、1 个判断项（已修复）；Spec 0 个发现；阶段一批准，阶段二未审批。
