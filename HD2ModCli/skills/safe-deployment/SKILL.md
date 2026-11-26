---
name: hd2mod-safe-deployment
description: Safely deploy or deactivate a Profile through audited planning, validation, and explicit confirmation.
---

# HD2 Mod Safe Deployment

Use only to apply an understood Profile to a stopped game. Only `deploy apply` writes GameData; this Skill is the narrow workflow for invoking it directly.

```powershell
# Standard plan-validate-apply audited workflow (fast mode enabled by default; use [--full] for physical SHA-256)
hd2mod deploy plan [profile-or-active] --manager-root "<manager>" --game-data "<data>" --output deployment-plan.json [--full] --json
hd2mod deploy validate --plan deployment-plan.json --manager-root "<manager>" --game-data "<data>" [--full] --json
hd2mod deploy apply --plan deployment-plan.json --manager-root "<manager>" --game-data "<data>" [--full] --confirm --json

# Direct deployment channel (bypasses plan artifact file; plans, validates, and applies in-memory)
hd2mod deploy apply [profile-or-active] --manager-root "<manager>" --game-data "<data>" [--full] --confirm --json

hd2mod deploy deactivate plan --manager-root "<manager>" --game-data "<data>" --output deactivation-plan.json --json
hd2mod deploy deactivate validate --plan deactivation-plan.json --manager-root "<manager>" --game-data "<data>" --json
hd2mod deploy deactivate apply --plan deactivation-plan.json --manager-root "<manager>" --game-data "<data>" --confirm --json
```

Before apply, review `patches`, `lua`, and `externalFiles` in the plan. Fast deployment mode (lightweight metadata fingerprinting `length:mtime` for unmodified patch payloads and fast link validation for hard links/symbolic links) is enabled by default to match GUI performance. Passing `--full` (or `--no-fast`) explicitly disables fast mode and calculates full physical SHA-256 hashes for all Patch payloads. Direct apply (`deploy apply [profile-or-active] --confirm`) bypasses writing and reading intermediate plan JSON artifacts on disk while reusing validated snapshots under lock. `lua.inputCount >= 2` means one generated Lua Patch may be materialized; Lua is syntax-compiled only and is never run locally. If diagnostics contain `LuaOpaqueMergeDisabled`, the global Manager setting is disabled; enable `允许不安全 Lua 合并部署` in the GUI, or rerun the CLI command with explicit `--allow-unsafe-lua`. This is a compatibility-risk acknowledgement, not a sandbox. `externalFiles` are manager-owned copies into the Game `bin` directory and must contain only expected source/target mappings. Plan and validation are read-only. Apply rechecks the plan under library and GameData leases, requires the manager and game to be closed, and refuses unknown or modified Patch ownership. Treat any blocking diagnostic as a stop condition.
