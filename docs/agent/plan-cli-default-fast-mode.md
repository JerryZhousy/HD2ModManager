# 规划文档：将 CLI 部署的比对与部署模式（--fast）设为默认

## 一、 背景与目标

在 HD2ModCli 的部署命令（`deploy plan`、`deploy validate`、`deploy apply`）中，最初的默认行为是对每一个待部署 Patch 计算物理全量 SHA-256 哈希值。当待部署 Mod 包含几十个 GB 的 Patch 时，单次命令行部署需要耗费十几秒甚至更久读取磁盘。
上周引入 `--fast` 开关后，通过轻量元数据指纹（`length:mtime`）与硬链接快速校验，使部署时间缩短至毫秒/秒级，体验与 GUI 保持一致。

本次目标：
1. 将 **`--fast`（极速比对与快速链接部署）直接设为默认行为**：用户无需额外携带 `--fast`，执行 `hd2mod deploy apply active --confirm` 或 `hd2mod deploy plan active --output plan.json` 即可自动享受极速体验；
2. 提供对称开关 **`--full` / `--no-fast`（全量物理 SHA-256 比对）**：当用户或审计流程需要强哈希校验与完整安全审计时，可显式传入 `--full` / `--no-fast` 回退到物理哈希计算；
3. 更新相关命令行帮助说明、Skill 文档和能力地图；
4. 补齐单元测试，验证默认快速模式以及显式 `--full` 模式，确保全量测试通过且 0 警告 0 错误。

---

## 二、 详细任务分解

### 1. 参数定义与解析调整 (`HD2ModCli/CliArguments.cs`)
- `CliGlobalOptions` 中：
  - 将 `bool Fast = false` 修改为 `bool Fast = true`。
- `ParseInvocation` 中：
  - `var fast = true;` 作为初始值；
  - 保留 `case "--fast": fast = true; break;` 兼容旧调用习惯；
  - 新增 `case "--full": case "--no-fast": case "--thorough": fast = false; break;`，允许显式关闭极速模式。

### 2. 命令行入口与帮助信息更新 (`HD2ModCli/CliApplication.cs`)
- 更新 `EnsureDeploymentPlanOptions`、`EnsureDeploymentValidateOptions`、`EnsureDeploymentApplyOptions`：
  - 放行 `--full` / `--no-fast` 选项，更新报错提示信息；
- 更新 `PrintHelp` 全局选项及示例：
  - 标记 `--fast` 为默认开启；
  - 补充 `--full / --no-fast` 为显式全量物理 SHA-256 审计模式。

### 3. 文档与 Skill 同步更新
- `HD2ModCli/skills/safe-deployment/SKILL.md`：
  - 更新命令说明，注明极速比对为默认行为；
  - 说明如需全量物理 SHA-256 审计，可追加 `--full`。
- `docs/agent/cli/HD2ModCli-capability-map.md`：
  - 记录 `--fast` 设为默认与 `--full` 开关。

### 4. 单元测试增补与交付门验收 (`HD2ModCli.Tests`)
- 在 `DeploymentCliTests.cs` 中增加：
  - 验证默认不带参数时执行极速模式（`doc.Patches` 无全量 `SourceSha256`）；
  - 验证带 `--full`（或 `--no-fast`）参数时执行全量哈希计算（`doc.Patches` 具有完整 `SourceSha256`）；
- 验证基线：
  - `dotnet build -c Debug` 0 警告 0 错误；
  - `dotnet test -c Debug` 全部通过。
