# 规划文档：HD2ModCli 库对账（library sync）性能优化与直通对账模式

## 一、 背景与性能瓶颈诊断

在真实 Mod 库测试中，执行：
```powershell
hd2mod library sync apply --plan out/library-sync-plan.json --manager-root "D:/Modding/HD2ModManager" --library "<ModsPath>" --confirm --json
```
耗时严重，甚至长达数分钟。

### 根本原因深度分析：
1. **全库 Patch 逐字节读取哈希风暴**：
   在 `HD2ModCli/CliLibrarySynchronizationPlanArtifacts.cs` 的 `ComputeInputSnapshotAsync` 中，遍历整库所有 Patch 文件时，调用了 `await SHA256.HashDataAsync(stream)` 逐字节读取计算文件 SHA-256。当 Mod 库有数十 GB 时，产生海量磁盘 I/O 阻塞。
2. **单次命令中 4 次重复整库扫描**：
   - 单次 `BuildAsync` 内部在前后分别计算 `before` 与 `after` 两次（全库读 2 遍）；
   - 在 `CliLibrarySynchronizationExecutor.ApplyAsync` 中，锁外调用了一次 `ValidateAsync`（包含一次 `BuildAsync`，全库读 2 遍），进入跨进程租约后又写着 `Repeat validation inside the cross-process lease`（再次调用 `ValidateAsync`，全库再读 2 遍）；
   - **整库在一次 apply 操作中被物理读取了整整 4 遍**！
3. **反观 GUI 为何极速**：
   GUI 的 `ModLibrarySynchronizer.cs` 在对账时，指纹仅基于 `RelativePath:Length:LastWriteTimeUtc.Ticks` 计算元数据指纹，根本不读文件内容，50 毫秒即可瞬间完成。

---

## 二、 改造目标与设计方案

1. **指纹轻量化（对齐默认 Fast 理念）**：
   - 在 `ComputeInputSnapshotAsync` 中引入 `bool fastMode = true`：
     - 当 `fastMode == true`（默认）：仅取文件的 `Path`、`Length` 和 `LastWriteTimeUtc.Ticks` 拼接哈希，**彻底免除任何 Patch 内容的文件流物理读取**；
     - 当 `fastMode == false`（显式 `--full` / `--no-fast` 时）：保留物理全量 SHA-256 计算。
2. **消除锁内 4 遍重复扫描（安全快照复用）**：
   - 在 `CliLibrarySynchronizationExecutor.ApplyAsync` 中，进入写租约后，检查 `library.json` 的修改时间/SHA256、待删除排除项以及顶层目录元数据是否发生漂移；
   - 若未发生漂移，直接安全复用锁前已经验证通过的构建结果，消除锁内第二次重复的全库二次扫描。
3. **支持直通免落盘对账**：
   - 像 `deploy apply` 一样，允许 `hd2mod library sync apply --confirm` 省略 `--plan` 参数，直接在内存中生成对账、验证并在租约内更新 `library.json`，无需外部中转 `plan.json` 文件。
4. **安全与隔离边界**：
   - **绝不触碰任何游戏目录（GameData）或部署逻辑**；
   - 维持跨进程库租约（`CliWriteTargetGuard.AcquireSessionAsync`）与 `--confirm` 安全门禁；
   - 节点消失后的残留空壳清理机制保持一致。

---

## 三、 详细任务分解

### 任务 1：核心指纹与构建轻量化 (`HD2ModCli/CliLibrarySynchronizationPlanArtifacts.cs`)
- `ComputeInputSnapshotAsync` 增加 `bool fastMode` 参数：
  - `fastMode == true` 时，计算 `Length:LastWriteTimeUtc.Ticks` 字符串哈希，替代打开文件流逐字节哈希；
  - 统计 `patchFileCount` 与 `patchByteCount` 保持完全一致；
- `BuildAsync` 增加 `bool fastMode = true` 参数，并透传给 `ComputeInputSnapshotAsync`；
- `ValidateAsync` 增加 `bool fastMode = true` 参数，并透传给 `BuildAsync`。

### 任务 2：执行器快照复用与直通对账 (`HD2ModCli/CliLibrarySynchronizationExecutor.cs`)
- 新增 `ApplyDirectAsync(CliWorkspace workspace, bool fastMode = true, CancellationToken cancellationToken = default)`：
  - 免 `--plan` 文件直通执行：在租约保护下直接执行对账并持久化；
- 在 `ApplyAsync` 中增加 `bool fastMode = true` 参数；
  - 锁内复用校验：验证前后元数据未漂移时，跳过重复的第二次全库扫描。

### 任务 3：命令行白名单与帮助支持 (`HD2ModCli/CliApplication.cs`)
- 更新 `EnsureLibrarySyncPlanOptions`、`EnsureLibrarySyncValidateOptions`、`EnsureLibrarySyncApplyOptions`：
  - 放行 `--fast`、`--full`、`--no-fast`；
  - `EnsureLibrarySyncApplyOptions` 允许省略 `--plan`（直通模式）；
- 在 `Matches(command, "library", "sync", "apply")` 中支持无 `--plan` 直通分支；
- 更新帮助信息与命令列表。

### 任务 4：Skill 与文档更新
- 更新 `HD2ModCli/skills/library-synchronization/SKILL.md`，追加直通模式与极速模式说明；
- 更新 `docs/agent/cli/HD2ModCli-capability-map.md`。

### 任务 5：测试覆盖与交付门验证
- 在 `HD2ModCli.Tests` 中编写专门的测试套件（直通对账测试、默认快速对账测试、`--full` 对账测试）；
- 交付门：`dotnet build -c Debug` 0 警告 0 错误；`dotnet test -c Debug` 全量测试通过。
