using System.Runtime.InteropServices;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：验证 ApplyPlanner 会基于真实索引按 Profile 顺序和 hex 分组生成连续目标编号。
// Purpose: Verifies ApplyPlanner builds continuous target numbering from the real index by profile order and archive hex.
public sealed class ApplyPlannerTests
{
	[Fact]
	public async Task BuildPlanAsync_RenumbersPatchFiles_PerArchiveHex()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var modsRoot = Path.Combine(root, "mods");
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(modsRoot);
		Directory.CreateDirectory(gameData);

		try
		{
			File.WriteAllText(Path.Combine(gameData, "9ba626afa44a3aa3.patch_9"), "foreign");
			var firstDir = Path.Combine(modsRoot, "first");
			var secondDir = Path.Combine(modsRoot, "second");
			Directory.CreateDirectory(firstDir);
			Directory.CreateDirectory(secondDir);
			File.WriteAllText(Path.Combine(firstDir, "9ba626afa44a3aa3.patch_3"), "a");
			File.WriteAllText(Path.Combine(firstDir, "9ba626afa44a3aa3.patch_3.stream"), "as");
			File.WriteAllText(Path.Combine(secondDir, "9ba626afa44a3aa3.patch_7"), "b");

			var firstId = ModNodeId.New();
			var secondId = ModNodeId.New();
			var first = new ModNode(
				Id: firstId,
				RelativePath: "first",
				Metadata: new ModNodeMetadata("first", null, DateTimeOffset.UtcNow, null),
				PatchGroups: Array.Empty<PatchGroupKey>(),
				Children: Array.Empty<ModNodeId>());
			var second = first with { Id = secondId, RelativePath = "second", Metadata = first.Metadata with { Name = "second" } };

			var snapshot = new LibrarySnapshot(2, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [firstId] = first, [secondId] = second }, Array.Empty<Profile>());
			var index = await new PatchFileIndexBuilder(new PatchFileNameParser()).BuildAsync(snapshot, modsRoot);
			var profile = new Profile(ProfileId.New(), "p", DateTimeOffset.UtcNow, null, new[]
			{
				new ProfileEntry(secondId, LoadOrder: -1, AddedUtc: DateTimeOffset.UtcNow),
				new ProfileEntry(firstId, LoadOrder: 0, AddedUtc: DateTimeOffset.UtcNow.AddSeconds(1)),
			});

			var planner = new ApplyPlanner(new PatchFileNameParser());
			var plan = await planner.BuildPlanAsync(profile, snapshot, index, gameData);

			Assert.Equal(Path.GetFullPath(gameData), plan.GameDataDirectory);
			Assert.Contains(plan.Operations, o => o.Kind == ApplyOperationKind.DeletePatch && o.TargetPath.EndsWith("9ba626afa44a3aa3.patch_9"));
			var deploys = plan.Operations.Where(o => o.Kind == ApplyOperationKind.DeployPatch).ToList();
			Assert.Equal(new int?[] { 0, 1, 1 }, deploys.Select(o => o.TargetPatchIndex).ToArray());
			Assert.Contains(deploys, o => o.NodeId == secondId && o.TargetPath.EndsWith("9ba626afa44a3aa3.patch_0"));
			Assert.Contains(deploys, o => o.NodeId == firstId && o.TargetPath.EndsWith("9ba626afa44a3aa3.patch_1"));
			Assert.Contains(deploys, o => o.NodeId == firstId && o.TargetPath.EndsWith("9ba626afa44a3aa3.patch_1.stream"));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	// 作用：目标位置已经是指向同一源文件的硬链接时，规划不应再发 DeletePatch（增量部署）。
	// Purpose: A target that already is a hard link to its source must not produce a DeletePatch.
	[Fact]
	public async Task BuildPlanAsync_SkipsDeletePatch_WhenTargetAlreadyLinksToSource()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var modsRoot = Path.Combine(root, "mods");
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(modsRoot);
		Directory.CreateDirectory(gameData);

		try
		{
			var modDirectory = Path.Combine(modsRoot, "only");
			Directory.CreateDirectory(modDirectory);
			var sourcePath = Path.Combine(modDirectory, "9ba626afa44a3aa3.patch_0");
			File.WriteAllText(sourcePath, "payload");

			// 已经部署过的那一份：data 里的同名文件是源文件的硬链接。
			var deployedPath = Path.Combine(gameData, "9ba626afa44a3aa3.patch_0");
			Assert.True(TryCreateHardLink(deployedPath, sourcePath), "无法在测试环境创建硬链接");
			// 与本次规划无关的残留文件，仍应被删除。
			var foreignPath = Path.Combine(gameData, "9ba626afa44a3aa3.patch_9");
			File.WriteAllText(foreignPath, "foreign");

			var nodeId = ModNodeId.New();
			var node = new ModNode(
				Id: nodeId,
				RelativePath: "only",
				Metadata: new ModNodeMetadata("only", null, DateTimeOffset.UtcNow, null),
				PatchGroups: Array.Empty<PatchGroupKey>(),
				Children: Array.Empty<ModNodeId>());
			var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [nodeId] = node }, Array.Empty<Profile>());
			var index = await new PatchFileIndexBuilder(new PatchFileNameParser()).BuildAsync(snapshot, modsRoot);
			var profile = new Profile(ProfileId.New(), "p", DateTimeOffset.UtcNow, null, new[]
			{
				new ProfileEntry(nodeId, LoadOrder: 0, AddedUtc: DateTimeOffset.UtcNow),
			});

			var plan = await new ApplyPlanner(new PatchFileNameParser()).BuildPlanAsync(profile, snapshot, index, gameData);

			Assert.DoesNotContain(plan.Operations, o => o.Kind == ApplyOperationKind.DeletePatch
				&& string.Equals(Path.GetFullPath(o.TargetPath), Path.GetFullPath(deployedPath), StringComparison.OrdinalIgnoreCase));
			Assert.Contains(plan.Operations, o => o.Kind == ApplyOperationKind.DeletePatch
				&& string.Equals(Path.GetFullPath(o.TargetPath), Path.GetFullPath(foreignPath), StringComparison.OrdinalIgnoreCase));
			// 部署操作仍然要发：激活状态靠它记录这一份文件。
			Assert.Contains(plan.Operations, o => o.Kind == ApplyOperationKind.DeployPatch
				&& string.Equals(Path.GetFullPath(o.TargetPath), Path.GetFullPath(deployedPath), StringComparison.OrdinalIgnoreCase));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CreateHardLinkW(string linkPath, string existingPath, IntPtr securityAttributes);

	private static bool TryCreateHardLink(string linkPath, string existingPath)
		=> OperatingSystem.IsWindows() && CreateHardLinkW(linkPath, existingPath, IntPtr.Zero);
}
