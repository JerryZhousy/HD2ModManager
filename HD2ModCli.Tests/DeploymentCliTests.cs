using System.Buffers.Binary;
using System.Text.Json;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.Lua;
using HD2ModCli;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;
using AdaptationPatchEntryPayload = HD2ModAdaptation.PatchReconstruction.PatchEntryPayload;
using AdaptationPatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;

namespace HD2ModCli.Tests;

[CollectionDefinition("cli-console", DisableParallelization = true)]
public sealed class CliConsoleCollection;

[Collection("cli-console")]
public sealed class DeploymentCliTests
{
	[Fact]
	public async Task DeployPlanValidateApply_EmptyProfile_CompletesThroughTheFullGuardedWorkflow()
	{
		await using var fixture = await DeploymentFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "deployment-plan.json");

		var planExit = await CliApplication.RunAsync(
		[
			"deploy", "plan", "active",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json",
		]);
		var validateExit = await CliApplication.RunAsync(
		[
			"deploy", "validate",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--plan", planPath,
			"--json",
		]);
		var applyExit = await CliApplication.RunAsync(
		[
			"deploy", "apply",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--plan", planPath,
			"--confirm",
			"--json",
		]);

		Assert.Equal(0, planExit);
		Assert.Equal(0, validateExit);
		Assert.Equal(0, applyExit);
		Assert.True(File.Exists(planPath));
		Assert.False(File.Exists(Path.Combine(fixture.GameDataDirectory, JsonActivationStateStore.StateFileName)));
	}

	[Fact]
	public async Task DeployPlanAndApply_RefuseUnmanagedPatchWithoutDeletingIt()
	{
		await using var fixture = await DeploymentFixture.CreateAsync();
		var foreignPatch = Path.Combine(fixture.GameDataDirectory, "0123456789abcdef.patch_0");
		await File.WriteAllTextAsync(foreignPatch, "FOREIGN");
		var planPath = Path.Combine(fixture.RootDirectory, "blocked-deployment-plan.json");

		var planExit = await CliApplication.RunAsync(
		[
			"deploy", "plan", "active",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json",
		]);
		var applyExit = await CliApplication.RunAsync(
		[
			"deploy", "apply",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--plan", planPath,
			"--confirm",
			"--json",
		]);

		Assert.Equal(2, planExit);
		Assert.Equal(2, applyExit);
		Assert.Equal("FOREIGN", await File.ReadAllTextAsync(foreignPatch));
	}

	[Fact]
	public async Task DeployPlanValidateApply_OneValidLuaPatch_DeploysTrackedPatchFiles()
	{
		await using var fixture = await DeploymentFixture.CreateAsync(includeLuaPatch: true);
		var planPath = Path.Combine(fixture.RootDirectory, "lua-deployment-plan.json");

		var planExit = await CliApplication.RunAsync(
		[
			"deploy", "plan", "active",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json",
		]);
		var applyExit = await CliApplication.RunAsync(
		[
			"deploy", "apply",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--plan", planPath,
			"--confirm",
			"--json",
		]);

		var state = await new JsonActivationStateStore().TryLoadAsync(fixture.GameDataDirectory);
		Assert.Equal(0, planExit);
		Assert.Equal(0, applyExit);
		Assert.NotNull(state);
		Assert.True(state!.Completed);
		Assert.Equal(3, state.Files.Count);
		Assert.All(state.Files, file => Assert.True(File.Exists(file.TargetPath)));
	}

	// 管理器不再合并第三方 Lua：两个都替换 boot 的补丁会被**原样**分别部署
	// （引擎只会执行序号最大的那份，这一点由计划里的 LuaBootEntryOverlap 警告告知用户）。
	[Fact]
	public async Task DeployPlanApply_TwoLuaPatches_DeploysBothAsIsWithoutGeneratingAMerge()
	{
		await using var fixture = await DeploymentFixture.CreateAsync(luaPatchCount: 2);
		var planPath = Path.Combine(fixture.RootDirectory, "two-lua-deployment-plan.json");

		var planExit = await CliApplication.RunAsync(
		[
			"deploy", "plan", "active",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json",
		]);
		var applyExit = await CliApplication.RunAsync(
		[
			"deploy", "apply",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--plan", planPath,
			"--confirm",
			"--json",
		]);

		var state = await new JsonActivationStateStore().TryLoadAsync(fixture.GameDataDirectory);
		Assert.Equal(0, planExit);
		Assert.Equal(0, applyExit);
		Assert.NotNull(state);
		// 两个补丁各带 base / stream / gpu_resources 三个文件，全部来自 mod 自己。
		Assert.Equal(6, state!.Files.Count);
		Assert.All(state.Files, file => Assert.DoesNotContain(Path.Combine(".runtime", "lua"), file.SourcePath, StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task DeployDeactivatePlanValidateApply_RemovesOnlyTheTrackedDeployment()
	{
		await using var fixture = await DeploymentFixture.CreateAsync(includeLuaPatch: true);
		var deployPlanPath = Path.Combine(fixture.RootDirectory, "deploy.json");
		var deactivatePlanPath = Path.Combine(fixture.RootDirectory, "deactivate.json");

		Assert.Equal(0, await CliApplication.RunAsync(
		[
			"deploy", "plan", "active", "--manager-root", fixture.ManagerRoot, "--game-data", fixture.GameDataDirectory, "--output", deployPlanPath, "--json",
		]));
		Assert.Equal(0, await CliApplication.RunAsync(
		[
			"deploy", "apply", "--manager-root", fixture.ManagerRoot, "--game-data", fixture.GameDataDirectory, "--plan", deployPlanPath, "--confirm", "--json",
		]));
		Assert.Equal(0, await CliApplication.RunAsync(
		[
			"deploy", "deactivate", "plan", "--manager-root", fixture.ManagerRoot, "--game-data", fixture.GameDataDirectory, "--output", deactivatePlanPath, "--json",
		]));
		Assert.Equal(0, await CliApplication.RunAsync(
		[
			"deploy", "deactivate", "validate", "--manager-root", fixture.ManagerRoot, "--game-data", fixture.GameDataDirectory, "--plan", deactivatePlanPath, "--json",
		]));
		Assert.Equal(0, await CliApplication.RunAsync(
		[
			"deploy", "deactivate", "apply", "--manager-root", fixture.ManagerRoot, "--game-data", fixture.GameDataDirectory, "--plan", deactivatePlanPath, "--confirm", "--json",
		]));

		Assert.False(File.Exists(Path.Combine(fixture.GameDataDirectory, JsonActivationStateStore.StateFileName)));
		Assert.Empty(Directory.EnumerateFiles(fixture.GameDataDirectory, "*.patch_*", SearchOption.TopDirectoryOnly));
	}

	[Fact]
	public async Task ExternalInspectionAndDeployment_ReShadeComponentsArePlannedTrackedAndSafelyRemoved()
	{
		await using var fixture = await DeploymentFixture.CreateAsync(luaPatchCount: 0, includeExternalComponents: true);
		var externalNodeId = Assert.IsType<ModNodeId>(fixture.ExternalNodeId);
		var deploymentPlanPath = Path.Combine(fixture.RootDirectory, "external-deploy.json");
		var deactivationPlanPath = Path.Combine(fixture.RootDirectory, "external-deactivate.json");
		var gameBin = Path.Combine(Directory.GetParent(fixture.GameDataDirectory)!.FullName, "bin");

		var inspection = await CliTestConsole.RunCapturedAsync(
			"mod", "external", "inspect", externalNodeId.Value.ToString("D"),
			"--library", fixture.LibraryRoot,
			"--json");
		Assert.Equal(0, inspection.ExitCode);
		using (var document = inspection.ParseJson())
		{
			var external = document.RootElement.GetProperty("external");
			Assert.Equal(2, external.GetProperty("files").GetArrayLength());
			Assert.Contains(external.GetProperty("files").EnumerateArray(), file => file.GetProperty("kind").GetString() == "reshadeplugin");
			Assert.Contains(external.GetProperty("files").EnumerateArray(), file => file.GetProperty("kind").GetString() == "reshadepreset");
			Assert.Contains(external.GetProperty("unrecognizedExternalPaths").EnumerateArray(), path => path.GetString() == "ReShade.ini");
		}

		Assert.Equal(0, await CliApplication.RunAsync(
		[
			"deploy", "plan", "active", "--manager-root", fixture.ManagerRoot, "--game-data", fixture.GameDataDirectory, "--output", deploymentPlanPath, "--json",
		]));
		var deploymentPlan = await CliDeploymentPlanArtifacts.ReadDocumentAsync(deploymentPlanPath);
		Assert.Equal(2, deploymentPlan.ExternalFiles.Count);
		Assert.Contains(deploymentPlan.ExternalFiles, file => file.Kind == ExternalInstallFileKind.ReShadePlugin && file.TargetRelativePath == "fixture.addon64");
		Assert.Contains(deploymentPlan.ExternalFiles, file => file.Kind == ExternalInstallFileKind.ReShadePreset && file.TargetRelativePath == "presets/fixture.ini");
		Assert.Equal(0, await CliApplication.RunAsync(
		[
			"deploy", "apply", "--manager-root", fixture.ManagerRoot, "--game-data", fixture.GameDataDirectory, "--plan", deploymentPlanPath, "--confirm", "--json",
		]));

		var state = await new JsonActivationStateStore().TryLoadAsync(fixture.GameDataDirectory);
		Assert.NotNull(state);
		Assert.Equal(2, state!.ExternalFiles.Count);
		Assert.Contains(state.ExternalFiles, file => file.Kind == ExternalInstallFileKind.ReShadePlugin && file.TargetRelativePath == "fixture.addon64");
		Assert.Contains(state.ExternalFiles, file => file.Kind == ExternalInstallFileKind.ReShadePreset && file.TargetRelativePath == "presets/fixture.ini");
		Assert.True(File.Exists(Path.Combine(gameBin, "fixture.addon64")));
		Assert.True(File.Exists(Path.Combine(gameBin, "presets", "fixture.ini")));
		Assert.False(File.Exists(Path.Combine(gameBin, "ReShade.ini")));

		Assert.Equal(0, await CliApplication.RunAsync(
		[
			"deploy", "deactivate", "plan", "--manager-root", fixture.ManagerRoot, "--game-data", fixture.GameDataDirectory, "--output", deactivationPlanPath, "--json",
		]));
		var deactivationPlan = await CliDeactivationPlanArtifacts.ReadDocumentAsync(deactivationPlanPath);
		Assert.Equal(2, deactivationPlan.ExternalFiles.Count);
		Assert.Contains(deactivationPlan.ExternalFiles, file => file.Kind == ExternalInstallFileKind.ReShadePlugin);
		Assert.Contains(deactivationPlan.ExternalFiles, file => file.Kind == ExternalInstallFileKind.ReShadePreset);
		Assert.Equal(0, await CliApplication.RunAsync(
		[
			"deploy", "deactivate", "apply", "--manager-root", fixture.ManagerRoot, "--game-data", fixture.GameDataDirectory, "--plan", deactivationPlanPath, "--confirm", "--json",
		]));
		Assert.False(File.Exists(Path.Combine(gameBin, "fixture.addon64")));
		Assert.False(File.Exists(Path.Combine(gameBin, "presets", "fixture.ini")));
	}

	[Fact]
	public async Task DeployApplyDirect_FastMode_DeploysWithoutPlanFile()
	{
		await using var fixture = await DeploymentFixture.CreateAsync(includeLuaPatch: true);

		var applyExit = await CliApplication.RunAsync(
		[
			"deploy", "apply", "active",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--fast",
			"--confirm",
			"--json",
		]);

		var state = await new JsonActivationStateStore().TryLoadAsync(fixture.GameDataDirectory);
		Assert.Equal(0, applyExit);
		Assert.NotNull(state);
		Assert.True(state!.Completed);
		Assert.Equal(3, state.Files.Count);
		Assert.All(state.Files, file => Assert.True(File.Exists(file.TargetPath)));
	}

	[Fact]
	public async Task DeployPlanAndApply_FastMode_ProducesFastPlanAndDeploys()
	{
		await using var fixture = await DeploymentFixture.CreateAsync(includeLuaPatch: true);
		var planPath = Path.Combine(fixture.RootDirectory, "fast-deployment-plan.json");

		var planExit = await CliApplication.RunAsync(
		[
			"deploy", "plan", "active",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--fast",
			"--json",
		]);
		var validateExit = await CliApplication.RunAsync(
		[
			"deploy", "validate",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--plan", planPath,
			"--fast",
			"--json",
		]);
		var applyExit = await CliApplication.RunAsync(
		[
			"deploy", "apply",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--plan", planPath,
			"--fast",
			"--confirm",
			"--json",
		]);

		var state = await new JsonActivationStateStore().TryLoadAsync(fixture.GameDataDirectory);
		Assert.Equal(0, planExit);
		Assert.Equal(0, validateExit);
		Assert.Equal(0, applyExit);
		Assert.NotNull(state);
		Assert.True(state!.Completed);
		Assert.Equal(3, state.Files.Count);
		Assert.All(state.Files, file => Assert.True(File.Exists(file.TargetPath)));

		// 验证导出的计划中，快速模式下的 Patch 未执行全量物理 SHA-256
		var planJson = await File.ReadAllTextAsync(planPath);
		var doc = JsonSerializer.Deserialize<CliDeploymentPlanDocument>(planJson, CliOutput.JsonOptions);
		Assert.NotNull(doc);
		Assert.All(doc!.Patches.Where(p => !p.IsGenerated), patch => Assert.Null(patch.SourceSha256));
	}

	[Fact]
	public async Task DeployPlan_DefaultBehavior_EnablesFastModeByDefault()
	{
		await using var fixture = await DeploymentFixture.CreateAsync(includeLuaPatch: true);
		var planPath = Path.Combine(fixture.RootDirectory, "default-fast-plan.json");

		// 不带 --fast 参数，验证默认即为 fast 模式
		var planExit = await CliApplication.RunAsync(
		[
			"deploy", "plan", "active",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json",
		]);

		Assert.Equal(0, planExit);
		var planJson = await File.ReadAllTextAsync(planPath);
		var doc = JsonSerializer.Deserialize<CliDeploymentPlanDocument>(planJson, CliOutput.JsonOptions);
		Assert.NotNull(doc);
		Assert.All(doc!.Patches.Where(p => !p.IsGenerated), patch => Assert.Null(patch.SourceSha256));
	}

	[Fact]
	public async Task DeployPlan_FullOption_PerformsFullPhysicalSha256()
	{
		await using var fixture = await DeploymentFixture.CreateAsync(includeLuaPatch: true);
		var planPath = Path.Combine(fixture.RootDirectory, "full-mode-plan.json");

		// 显式带 --full 参数，要求执行全量物理 SHA-256
		var planExit = await CliApplication.RunAsync(
		[
			"deploy", "plan", "active",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--full",
			"--json",
		]);

		Assert.Equal(0, planExit);
		var planJson = await File.ReadAllTextAsync(planPath);
		var doc = JsonSerializer.Deserialize<CliDeploymentPlanDocument>(planJson, CliOutput.JsonOptions);
		Assert.NotNull(doc);
		Assert.All(doc!.Patches.Where(p => !p.IsGenerated), patch => Assert.False(string.IsNullOrWhiteSpace(patch.SourceSha256)));
	}

	[Fact]
	public async Task DeployValidate_ReusesPreviousSnapshot_WhenNoDriftOccurs()
	{
		await using var fixture = await DeploymentFixture.CreateAsync(includeLuaPatch: true);
		var workspace = CliWorkspace.Open(new CliGlobalOptions(
			ManagerRoot: fixture.ManagerRoot,
			Library: null,
			GameData: fixture.GameDataDirectory,
			RegistryRoot: null,
			Output: null,
			Plan: null,
			Host: null,
			AddToProfile: null,
			Keys: null,
			AddKeys: null,
			RemoveKeys: null,
			ArchiveId: null,
			Kind: null,
			Header: null,
			Picks: [],
			Position: null,
			Limit: null,
			Cursor: null,
			SetActive: false,
			WithUnsharedOptions: false,
			WithEffective: false,
			OnlyCompetition: false,
			Overwrite: false,
			Confirm: false,
			Json: false));

		var initialBuild = await CliDeploymentPlanArtifacts.BuildAsync(workspace, "active", fastMode: true);
		Assert.NotNull(initialBuild.Document);

		// 使用相同的 previous 调用 ValidateAsync，应命中轻量快速检查并直接复用实例
		var validation = await CliDeploymentPlanArtifacts.ValidateAsync(
			initialBuild.Document,
			workspace,
			includeProcessCheck: true,
			fastMode: true,
			previous: initialBuild);

		Assert.True(validation.IsValid);
		Assert.Same(initialBuild, validation.Current);
	}

	[Fact]
	public async Task DeployBuild_FastMode_UsesAndPersistsHashCacheForOwnershipInspection()
	{
		await using var fixture = await DeploymentFixture.CreateAsync(includeLuaPatch: true);
		var applyExit = await CliApplication.RunAsync(
		[
			"deploy", "apply", "active",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--fast",
			"--confirm",
			"--json",
		]);
		Assert.Equal(0, applyExit);

		// 模拟旧版或 --full/Copy 部署留下的带真实 ContentSha256 的 activation-state.json
		var stateStore = new JsonActivationStateStore();
		var state = await stateStore.TryLoadAsync(fixture.GameDataDirectory);
		Assert.NotNull(state);
		var filesWithHashes = new List<ActivationStateFileEntry>(state!.Files.Count);
		foreach (var entry in state.Files)
		{
			var bytes = await File.ReadAllBytesAsync(entry.SourcePath);
			var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
			filesWithHashes.Add(entry with { ContentSha256 = sha });
		}
		await stateStore.SaveAsync(fixture.GameDataDirectory, state with { Files = filesWithHashes });

		var cachePath = Path.Combine(fixture.ManagerRoot, "data", "indexes", "patch-hashes.json");
		if (File.Exists(cachePath)) File.Delete(cachePath);

		var workspace = CliWorkspace.Open(new CliGlobalOptions(
			ManagerRoot: fixture.ManagerRoot,
			Library: null,
			GameData: fixture.GameDataDirectory,
			RegistryRoot: null,
			Output: null,
			Plan: null,
			Host: null,
			AddToProfile: null,
			Keys: null,
			AddKeys: null,
			RemoveKeys: null,
			ArchiveId: null,
			Kind: null,
			Header: null,
			Picks: [],
			Position: null,
			Limit: null,
			Cursor: null,
			SetActive: false,
			WithUnsharedOptions: false,
			WithEffective: false,
			OnlyCompetition: false,
			Overwrite: false,
			Confirm: false,
			Json: false));

		var firstBuild = await CliDeploymentPlanArtifacts.BuildAsync(workspace, "active", fastMode: true);
		Assert.NotNull(firstBuild.OwnershipInspection);
		Assert.True(firstBuild.OwnershipInspection!.IsManaged);
		Assert.True(File.Exists(cachePath));

		// 修改一个硬链接源文件但保持长度和 LastWriteTimeUtc 不变：
		// 只有真正命中了 CliPatchHashCache 才不会在所有权校验阶段重读磁盘并报 ManagedPatchHashMismatch。
		var targetEntry = filesWithHashes.First(entry => entry.Method == DeploymentMethod.HardLink);
		var originalBytes = await File.ReadAllBytesAsync(targetEntry.SourcePath);
		var lastWrite = File.GetLastWriteTimeUtc(targetEntry.SourcePath);
		originalBytes[0] ^= 0xFF;
		await File.WriteAllBytesAsync(targetEntry.SourcePath, originalBytes);
		File.SetLastWriteTimeUtc(targetEntry.SourcePath, lastWrite);

		var secondBuild = await CliDeploymentPlanArtifacts.BuildAsync(workspace, "active", fastMode: true);
		Assert.NotNull(secondBuild.OwnershipInspection);
		Assert.True(secondBuild.OwnershipInspection!.IsManaged);
		Assert.DoesNotContain(secondBuild.OwnershipInspection.Issues, issue => issue.Code == "ManagedPatchHashMismatch");
	}

	private sealed class DeploymentFixture : IAsyncDisposable
	{
		private DeploymentFixture(string rootDirectory, string managerRoot, string libraryRoot, string gameDataDirectory, ModNodeId? externalNodeId)
		{
			RootDirectory = rootDirectory;
			ManagerRoot = managerRoot;
			LibraryRoot = libraryRoot;
			GameDataDirectory = gameDataDirectory;
			ExternalNodeId = externalNodeId;
		}

		public string RootDirectory { get; }
		public string ManagerRoot { get; }
		public string LibraryRoot { get; }
		public string GameDataDirectory { get; }
		public ModNodeId? ExternalNodeId { get; }

		public static ValueTask<DeploymentFixture> CreateAsync(bool includeLuaPatch = false, bool includeExternalComponents = false)
			=> CreateAsync(includeLuaPatch ? 1 : 0, includeExternalComponents);

		public static async ValueTask<DeploymentFixture> CreateAsync(int luaPatchCount, bool includeExternalComponents = false)
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-cli-deploy-" + Guid.NewGuid().ToString("N"));
			var manager = Path.Combine(root, "manager");
			var library = Path.Combine(root, "library");
			var gameRoot = Path.Combine(root, "Helldivers 2");
			var gameData = Path.Combine(gameRoot, "data");
			Directory.CreateDirectory(manager);
			Directory.CreateDirectory(Path.Combine(gameRoot, "bin"));
			Directory.CreateDirectory(gameData);
			await File.WriteAllTextAsync(Path.Combine(gameRoot, "bin", "helldivers2.exe"), string.Empty);
			await File.WriteAllTextAsync(
				Path.Combine(manager, "settings.json"),
				JsonSerializer.Serialize(new
				{
					modLibraryFolder = library,
					gameDataFolder = gameData,
					// 这些用例描述的是"一个第三方 Lua 补丁怎么被原样装进去"，与映射/物理模式无关。
					// 显式钉住两个模式，别让产品默认值变化（2026-11-03 物理默认改成「独占 boot」）
					// 把它们带到"准备物理运行时"那条路上——那条路要真实的游戏 boot 归档。
					armorMappingMode = (int)ArmorMappingMode.ExclusiveLuaEntry,
					physicsMode = (int)PhysicsMode.Off,
				}));

			var nodes = new Dictionary<ModNodeId, ModNode>();
			var entries = new List<ProfileEntry>();
			for (var index = 0; index < luaPatchCount; index++)
			{
				var nodeId = ModNodeId.New();
				var relativePath = $"lua-mod-{index + 1}";
				var modDirectory = Path.Combine(library, relativePath);
				Directory.CreateDirectory(modDirectory);
				var patchPath = Path.Combine(modDirectory, $"{LuaAssetKeys.BootArchiveId:x16}.patch_0");
				await WriteFixtureLuaPatchAsync(patchPath);
				nodes[nodeId] = new ModNode(
					nodeId,
					relativePath,
					new ModNodeMetadata("Lua Fixture", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
					[new PatchGroupKey($"{LuaAssetKeys.BootArchiveId:x16}", 0)],
					[]);
				entries.Add(new ProfileEntry(nodeId, 0, DateTimeOffset.UtcNow));
			}
			ModNodeId? externalNodeId = null;
			if (includeExternalComponents)
			{
				var nodeId = ModNodeId.New();
				externalNodeId = nodeId;
				var relativePath = "reshade-fixture";
				var modDirectory = Path.Combine(library, relativePath);
				Directory.CreateDirectory(Path.Combine(modDirectory, "bin", "presets"));
				await File.WriteAllBytesAsync(Path.Combine(modDirectory, "fixture.addon64"), [0x01, 0x02, 0x03]);
				await File.WriteAllTextAsync(Path.Combine(modDirectory, "bin", "presets", "fixture.ini"), "Techniques=FixtureTechnique\nTechniqueSorting=FixtureTechnique\n");
				await File.WriteAllTextAsync(Path.Combine(modDirectory, "ReShade.ini"), "Techniques=ThisGlobalConfigurationMustNotDeploy\n");
				nodes[nodeId] = new ModNode(
					nodeId,
					relativePath,
					new ModNodeMetadata("ReShade Fixture", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ModNodeKind.ReShade),
					[],
					[]);
				entries.Add(new ProfileEntry(nodeId, entries.Count, DateTimeOffset.UtcNow));
			}
			var profile = new Profile(ProfileId.New(), "Default", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, entries);
			var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow, nodes, [profile], profile.Id);
			await new JsonModLibraryStore(new StoragePaths(manager, library), allowLegacyMigration: false).SaveAsync(snapshot);
			return new DeploymentFixture(root, manager, library, gameData, externalNodeId);
		}

		private static async ValueTask WriteFixtureLuaPatchAsync(string patchPath)
		{
			var header = new byte[0xc0];
			BinaryPrimitives.WriteUInt32LittleEndian(header, 0xF0000011);
			BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
			BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 1);
			// One legacy TOC type record at 0x3c and one entry at 0x5c.
			BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x44), LuaAssetKeys.LuaTypeId);
			BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x4c), 1);
			BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x5c), LuaAssetKeys.BootFileId);
			BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x64), LuaAssetKeys.LuaTypeId);
			var entry = new AdaptationPatchTocEntry(LuaAssetKeys.BootAssetKey, patchPath, Path.GetFileName(patchPath));
			var document = new LuaPatchDocument(
				patchPath,
				entry,
				new AdaptationPatchEntryPayload(entry, [], [], []),
				header,
				[0x1b, 0x4c, 0x4a],
				[new LuaPatchBlock("fixture", "local fixture = true")]);
			var composer = new LuaPatchComposer();
			var composition = composer.Wrap(document);
			await composer.WriteAsync(patchPath, composition);
		}

		public ValueTask DisposeAsync()
		{
			try { Directory.Delete(RootDirectory, recursive: true); } catch { }
			return ValueTask.CompletedTask;
		}
	}
}
