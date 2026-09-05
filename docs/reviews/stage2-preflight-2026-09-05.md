# 阶段二正式矩阵前预检记录

日期：2026-09-05

状态：预检通过，阶段二暂不审批

代码基线：`04cf4eb33fb00a9c5da8100e0b42db38fd82851c`

## 已通过门禁

- 本地与 GitHub `main` 均指向 `04cf4eb33fb00a9c5da8100e0b42db38fd82851c`，工作树干净。
- 阶段一审批已通过；后续只读复核为 0 个实质 findings。
- Release Timing 与 Development GC 均由 Unity `6000.5.9f1 (b57deb96f08d)` 构建为 Windows x64 IL2CPP Player。
- 两次最终构建都验证 Collections `6.5.0 / builtin` 与 packages-lock SHA-256 `5496DF13106EE5FE221DD76C28BE21048FA43163AF6C657FF034EE7FE2B779E6`，日志均出现 `VERIFIED_ENV`、`Build Finished, Result: Success` 与 `COMPLETE`。
- Release 构建树为 443,375,678 bytes；Player EXE SHA-256 为 `F95AE8934E9BF68761AAF83B82E9FAF641666B6B1FC923EEF4F54336D07EA28C`；build GUID 为 `32ce296ca75d415d941a8799b2b057dc`。
- Development 构建树为 515,524,991 bytes；Player EXE SHA-256 为 `638DCFD5F2174A1F8800F1DB22E978D538F2664548964D3F553075105E605FB9`；build GUID 为 `3cf1724fbf844bdab43d872f09544f33`。

## Player 预检结果

| 门禁 | 期望 | 实际 | 结果 |
|---|---|---|---|
| Release smoke + `-benchmarkTimingOnly` | Completed / 19/19 / exit 0 | Completed / 19/19 / exit 0 | 通过 |
| Development smoke，不带 timing-only | Completed / 19/19 / exit 0 | Completed / 19/19 / exit 0，GC calibrated | 通过 |
| Release 缺少 timing-only | EnvironmentFailed / exit 1 | EnvironmentFailed / exit 1 | 通过 |
| Development 错带 timing-only | EnvironmentFailed / exit 1 | EnvironmentFailed / exit 1 | 通过 |
| `-autoRun` 缺少 Smoke/Full/Shard | 参数拒绝 / exit 2 | 参数拒绝 / exit 2 | 通过 |

正向 smoke 均为 `representative-smoke-v1`、19 个用例、38 条结果行、0 failed、0 skipped；每个非预热结果包含 10 个稳态样本。Release 仅提供 TimingOnly，Development 仅提供 DevelopmentGC。

预检 JSON SHA-256：

- Release smoke：`E3773713A61ABD61BA24FC43B9CA72D87CB36E1A4EA195A0603832EA999F5710`
- Development smoke：`FB438897C39A536E00971E7E5A2950CAE7806334AEA7B42BC65BB47398A96769`
- Release negative：`D2560CF31825C8904002FDB3BA78437BF11714C0F79A0694884217881FF57FA0`
- Development negative：`730FF3AC6FEC81C5AFE6383349BB74EB47A948346F995443BADE501CE47BFC9D`

## 当前暂停点

正式矩阵尚未启动。预检时检测到 `D:\Project\sanguo3` 的 Unity 2022.3 Editor、两个 AssetImportWorker 以及 Rider 后端仍在运行。正式计时要求隔离 CPU/GPU/磁盘竞争，因此不能把当前环境下的结果作为最终性能证据。

## 恢复后剩余步骤

1. 确认其他 Unity Editor、Profiler、游戏和高负载 IDE 后端均已退出。
2. 串行执行 Release Timing 的 `linear-small`、`linear-large`、`quadratic-stress` 三个分片。
3. 串行执行 Development GC 的同三个分片。
4. 验证 6 个最终 JSON、6 个 checkpoint、6 个单通道 HTML、2 个 matrix manifest；总计 1,856 次用例执行、3,712 条源结果行，0 failed、0 skipped。
5. 汇总为 928 个唯一用例的双通道 envelope，生成离线单文件 HTML 与易懂 Markdown 对照分析。
6. 完成浏览器交互/脚本检查后，再进行阶段二最终审批并提交、推送最终证据。

本文件只记录预检和暂停点，不构成阶段二批准。
