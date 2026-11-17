# HD2ModManager CLI 与 GUI 部署性能差异分析报告

> 针对老大关注的问题：“为什么 CLI 的部署等都比 GUI 慢那么多（GUI 只需要几秒）？”  
> 本报告从架构设计、调用链路、I/O 行为与缓存机制四个维度进行代码级全景对比。

---

## 核心结论速览

GUI 部署只需几秒，而 CLI 需要十几秒甚至一两分钟，其本质原因是**两者在安全模型、校验粒度与进程生命周期上的根本定位差异**，并伴随 CLI 代码实现中的**参数遗漏**与**高冗余重复验证**：

1. **【最直接的性能断崖：GUI 开启零哈希快检，CLI 遗漏开关触发全量哈希】**：
   - GUI 在硬链接部署时显式启用了 `UseFastLinkValidation = true`，部署前后**完全跳过对文件的 SHA-256 计算**，只做毫秒级的文件系统硬链接与长度校验。
   - CLI 在 `CliDeploymentExecutor.ApplyAsync` 中**漏掉了该标志**（默认为 `false`），导致即使采用硬链接，底层 `ApplyExecutor` 依然会对所有待部署文件和部署后文件执行 SHA-256 计算与校验。
2. **【架构级开销：CLI 在单次部署中重复执行了 2~3 次全流程构建（BuildAsync）】**：
   - CLI 追求不可篡改的审计防御：在 `apply` 加锁前跑了一次完整 `ValidateAsync`（内部完整执行 `BuildAsync`），加锁后又跑了一次完整 `ValidateAsync`（又执行一次 `BuildAsync`），紧接着 `MaterializeAsync` 又做了一遍映射。单次 `apply` 内就把整个规划流程重复跑了 2~3 遍。
3. **【审计指纹设计：CLI 必须为每一个 Patch 与输入状态生成 SHA-256】**：
   - CLI 的 `deploy plan` 要求把每个 Patch 的 SHA-256 固化到计划中，并计算全局输入指纹 `InputFingerprint`。首次部署或 Mod 库更新时，必须硬读全部文件计算哈希（数 GB 至数十 GB 的磁盘 I/O）；即便有 `CliPatchHashCache` 缓存，缓存的比对与序列化也是额外开销。
4. **【运行态差异：GUI 拥有常驻内存热缓存，CLI 每次都是冷进程全量扫描】**：
   - GUI 常驻运行，`IModInformationCenter` 和 `LibrarySnapshot` 热常驻在内存中，Mod 索引、ContentFacts 均无需重复解析；且 GUI 默认信任当前状态，不会对 GameData 做全量 Ownership 扫描。
   - CLI 每次都是独立的冷启动进程，必须重读反序列化 `library.json`、物理遍历扫描 `mods/` 目录构建文件索引、扫描 `game/data` 目录进行所有权鉴权（一次 apply 扫描两次）。

---

## 详细代码对比与证据链

### 1. 关键差异一：`UseFastLinkValidation` 开关的启用与遗漏

#### GUI 路径（`HD2ModCore\Infrastructure\ProfileApplyService.cs` 第 100~105 行）：
```csharp
plan = plan with
{
    DeploymentMethod = capability.Method.Value,
    // 硬链接或符号链接时开启极速验证：
    UseFastLinkValidation = capability.Method.Value is DeploymentMethod.HardLink or DeploymentMethod.SymbolicLink,
};
return await _executor.ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
```

#### CLI 路径（`HD2ModCli\CliDeploymentExecutor.cs` 第 76 行）：
```csharp
// CLI 组装执行计划时，直接复制了物化计划，漏掉了 UseFastLinkValidation！
var executionPlan = materialized.ApplyPlan! with { DeploymentMethod = capability.Method.Value };
```

#### 底层 `ApplyExecutor` 中的连锁反应（`HD2ModCore\Infrastructure\ApplyExecutor.cs`）：
- **预检阶段（`PreflightAsync` 第 296~301 行）**：
  ```csharp
  var sha256 = useFastLinkValidation
      ? string.Empty // GUI 走这里：直接返回空字符串，耗时 0ms！
      : contentHasher is null
          ? await ComputeSha256Async(operation.SourcePath, cancellationToken).ConfigureAwait(false)
          : await contentHasher(operation.SourcePath, cancellationToken).ConfigureAwait(false);
  ```
- **部署后验证阶段（`VerifyDeployedFilesAsync` 第 701 行）**：
  ```csharp
  if (useFastLinkValidation) continue; // GUI 走这里：直接跳过，耗时 0ms！
  if (string.IsNullOrWhiteSpace(file.ContentSha256)) { ... }
  // CLI 走这里：如果为 false，对每个文件都要核验哈希！
  var hash = contentHasher is null
      ? await ComputeSha256Async(hashPath, CancellationToken.None).ConfigureAwait(false)
      : await contentHasher(hashPath, CancellationToken.None).ConfigureAwait(false);
  ```
> **结果**：GUI 在部署上百个 Patch 时，纯粹依靠 Windows NTFS 的 `CreateHardLink` 系统调用与 `FileInfo.Length` 元数据核验，几秒钟瞬间收工；CLI 则会被拖入全量哈希预检与部署后哈希比对。

---

### 2. 关键差异二：CLI 的多重重构（冗余验证机制）

在 `HD2ModCli\CliDeploymentExecutor.cs` 中，为了防止任何外部并发篡改或配置漂移，CLI 采用了极严格的双重加锁验证：

```csharp
// 1. 获取锁前校验（第 27 行）：
var firstValidation = await CliDeploymentPlanArtifacts.ValidateAsync(document, workspace, includeProcessCheck: true, cancellationToken);

// ...获取进程锁、Mod 库租约与 GameData 租约...

// 2. 获取锁后再次校验（第 45 行）：
var lockedValidation = await CliDeploymentPlanArtifacts.ValidateAsync(document, workspace, includeProcessCheck: true, cancellationToken);

// 3. 物化计划（第 58 行）：
var materialized = await CliDeploymentPlanArtifacts.MaterializeAsync(current, cancellationToken);
```

然而，我们看 `ValidateAsync` 内部（`HD2ModCli\CliDeploymentPlanArtifacts.cs` 第 333 行）：
```csharp
public static async ValueTask<CliDeploymentValidationResult> ValidateAsync(...)
{
    // 每次验证，都完完整整地重新 Build 了一次！
    current = await BuildAsync(workspace, document.Profile.Id, cancellationToken).ConfigureAwait(false);
    // ...
}
```

而每一次 `BuildAsync` 都要依序执行：
1. `workspace.LoadLibraryAsync`（读 library.json 与 option-activations.json）
2. `DeploymentOwnershipInspector.InspectAsync`（扫描 GameData 顶层所有文件验证归属）
3. `PatchFileIndexBuilder.BuildAsync`（磁盘遍历扫描 Mod 库目录）
4. `ReadActiveContentFactsAsync`（读取所有活动 Mod 的物理事实与资源清单）
5. `LuaDeploymentService.PlanAsync`（规划 Lua 注入）
6. `ApplyPlanner.BuildPlanAsync`（构建基础 Patch 部署计划）
7. `CreatePatchProjectionsAsync`（遍历所有 Patch，从缓存查/算哈希并投影）
8. `ComputeInputFingerprintAsync`（计算总体输入状态的增量 SHA-256 指纹）

**单单执行一次 `hd2mod deploy apply`，上述 8 大步骤就被执行了整整 2 遍！紧接着 `MaterializeAsync` 又执行了第 3 遍计划构建！** 如果加上前置的 `hd2mod deploy plan`，整个重构管线跑了 3~4 遍。

---

### 3. 关键差异三：审计指纹需求 vs 极速交付需求

- **CLI 的核心使命**是自动化与外部调用，要求提供审计追踪证据：
  - 它必须输出一份自洽的 `deployment-plan.json`。
  - 必须对每个输入项（ModNode、Patch 路径、序号、内容 SHA-256）做哈希，最终合并成一个全局的 `InputFingerprint`。
  - 虽然开发团队引入了 `CliPatchHashCache`（`data\indexes\patch-hashes.json`），缓解了冷读磁盘的问题（如源码注释所言：*“实测 546 个 Patch / 9.3 GB 时单次规划约 84 秒”*），但在任何缓存未命中或初次使用时，CLI 依然必须承受数十 GB 的真实 I/O 开销。
- **GUI 的核心使命**是给玩家即时反馈：
  - 玩家点击“部署”只关心“游戏能不能马上玩上最新 Mod”。
  - 不需要导出自包含审计文档，不需要输入防伪指纹，不需要对几百个 Patch 文件提取 SHA-256。

---

### 4. 关键差异四：常驻内存 vs 冷进程启动与 GameData 扫描

| 维度 | GUI (HD2ModManager) | CLI (HD2ModCli) |
| :--- | :--- | :--- |
| **进程生命周期** | 常驻进程，启动一次后持续在后台运行 | 每次命令都是全新进程，.NET CLR 冷启动 |
| **Mod 库状态** | `LibrarySnapshot` 驻留内存，变更时增量更新 | 每次从磁盘重新反序列化数 MB 的 JSON |
| **文件事实缓存** | `IModInformationCenter` 单例常驻内存，`FileFacts` 毫秒级命中 | 每次重新通过 `PatchFileIndexBuilder` 遍历扫描磁盘 |
| **GameData 目录检查** | 默认不扫描（代码注释明确标注为显式诊断，非部署前置操作） | 每次部署规划都调用 `DeploymentOwnershipInspector` 全量扫描 GameData 目录 |
| **哈希缓存** | 无需哈希缓存（硬链接直连） | 每次启动读取 `patch-hashes.json`，退出前序列化写入 |

---

## 优化方向与改进建议（供后续参考）

若未来需要提升 CLI 的部署性能至 GUI 同级别，可考虑以下几项定向改造：
1. **补齐开关**：在 `CliDeploymentExecutor.ApplyAsync` 中，为 `materialized.ApplyPlan` 补齐 `UseFastLinkValidation = capability.Method.Value is DeploymentMethod.HardLink or DeploymentMethod.SymbolicLink`。
2. **复用上下文避免重复 `BuildAsync`**：
   - `ValidateAsync` 目前把 `BuildAsync` 当作黑盒重跑。实际上在锁内只需要验证“自导出计划以来，ProfileRevision、文件哈希缓存和 GameData 租约是否发生漂移”，无需从头扫描 Mod 库与重新计算指纹。
3. **轻量化 Apply 模式**：
   - 提供类似 `--fast` 或单命令 `deploy apply active --fast`，跳过导出前置的细粒度 Patch SHA-256 计算，直接复用 GUI 的极速部署管道。
