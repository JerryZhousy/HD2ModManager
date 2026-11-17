# CLI 部署性能三重优化工程规划

## 一、 背景与目标

根据老大指示，针对 CLI 部署速度远慢于 GUI 的现状，落地三项核心优化：
1. **补齐快速验证开关**：在 `CliDeploymentExecutor.ApplyAsync` 中补上 `UseFastLinkValidation`，在硬链接/软链接可用时消除底层的二次哈希计算。
2. **消灭重复 BuildAsync**：优化 `ValidateAsync` 机制，在锁内仅对轻量快照与关键元数据进行漂移核验，而非把整个耗时的物理构建重跑两遍。
3. **引入 --fast 模式**：为 CLI 增加类似 GUI 的免哈希直通部署通道，既支持单命令直通部署（免 `--plan` 文件中转），也支持快速计划生成与应用。

---

## 二、 影响范围与模块设计

### 1. 任务一：补齐 `UseFastLinkValidation` 开关
- **涉及文件**：
  - `HD2ModCli/CliDeploymentExecutor.cs`
- **实施方案**：
  在第 76 行构造 `executionPlan` 时，保持与 `ProfileApplyService.cs` 一致的口径：
  ```csharp
  var executionPlan = materialized.ApplyPlan! with
  {
      DeploymentMethod = capability.Method.Value,
      UseFastLinkValidation = capability.Method.Value is DeploymentMethod.HardLink or DeploymentMethod.SymbolicLink,
  };
  ```
- **收益**：硬链接与符号链接部署时，底层 `ApplyExecutor.PreflightAsync` 和 `VerifyDeployedFilesAsync` 直接跳过对每一个 Patch 文件的 SHA-256 读取与比对。

---

### 2. 任务二：消灭锁内重复 `BuildAsync`
- **涉及文件**：
  - `HD2ModCli/CliDeploymentPlanArtifacts.cs`
  - `HD2ModCli/CliDeploymentExecutor.cs`
- **核心逻辑**：
  - 目前在 `CliDeploymentExecutor.ApplyAsync` 中：
    - 锁前：调用 `CliDeploymentPlanArtifacts.ValidateAsync`，内部执行 `BuildAsync`，生成 `firstValidation.Current`。
    - 锁后：再次调用 `CliDeploymentPlanArtifacts.ValidateAsync`，内部又完整执行了一次 `BuildAsync`。
  - **优化方案**：
    在 `CliDeploymentPlanArtifacts` 中引入轻量级快照验证逻辑 `ValidateDriftAsync`：
    - 输入：前一次验证产出的 `CliDeploymentBuildResult current`、`CliDeploymentPlanDocument document`、`CliWorkspace workspace`、`includeProcessCheck`。
    - 检查项：
      1. 游戏运行进程检查（`CliDeploymentProcessGuard`）；
      2. 目标路径有效性（`GameDataTargetGuard` 是否与前次一致）；
      3. `library.json` 的修改时间与 ActiveProfile Revision 是否变动；
      4. `activation-state.json` 的修改时间与状态是否变动。
    - 若各项元数据未发生漂移，则直接复用 `current`，生成合法的 `lockedValidation`，无需再次触发耗时磁盘扫描与指纹计算；
    - 仅在检测到元数据变动（极端并发外部篡改）时，才回退到全量 `BuildAsync`。
- **收益**：单次 `deploy apply` 的 `BuildAsync` 执行次数由 2 次减为 1 次，锁内耗时降至毫秒级。

---

### 3. 任务三：引入 `--fast` 免哈希直通模式
- **涉及文件**：
  - `HD2ModCli/CliArguments.cs`：在 `CliGlobalOptions` 中增加 `bool Fast = false`；在参数解析循环中识别 `--fast`。
  - `HD2ModCli/CliApplication.cs`：
    - 更新 `EnsureDeploymentApplyOptions` 与 `EnsureDeploymentPlanOptions`，允许接收 `--fast` 开关；
    - 扩展 `deploy apply` 命令语法：支持免 `--plan` 直通部署：
      `hd2mod deploy apply [配置标识或省略表示active] --confirm --fast`（同时兼容 `--plan <file> --confirm --fast`）；
    - 更新帮助文档。
  - `HD2ModCli/CliDeploymentPlanArtifacts.cs`：
    - 在 `BuildAsync` 中支持 `bool fastMode = false` 参数：
      在快速模式下，跳过 `GetHashAsync` 的物理文件哈希计算，使用 `$"fast:{length}:{mtime}"` 作为轻量指纹，使 `deploy plan --fast` 与直通部署能在 1 秒内完成。
- **收益**：玩家或自动化脚本可通过单条命令 `hd2mod deploy apply active --confirm --fast` 在数秒内完成部署，达到与 GUI 完全一致的极速体验。

---

## 三、 测试与验收准则（交付门）

1. **测试基线与覆盖**：
   - 现存所有 202 个 `HD2ModCli.Tests` 必须继续 100% 通过；
   - 新增针对 `--fast` 选项、免 `--plan` 直通部署、锁内快速比对不发生重复 `BuildAsync` 的单元/集成测试。
2. **编译质量**：
   - `dotnet build -c Debug` 必须 **0 警告 0 错误**（警告视为错误）。
   - `dotnet test -c Debug` 全量测试全绿。
