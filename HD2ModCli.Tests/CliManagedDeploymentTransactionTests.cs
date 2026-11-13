using System.Runtime.InteropServices;
using HD2ModCli;
using HD2ModCore.Domain;
using Xunit;

namespace HD2ModCli.Tests;

// 作用：验证部署事务只搬"这次真的要动"的 Patch，并且回滚不会误删没动过的文件。
// Purpose: Verifies the deployment transaction only moves patches this deploy will touch, and that a rollback
// never deletes a file the transaction did not take.
public sealed class CliManagedDeploymentTransactionTests
{
	[Fact]
	public async Task BeginAsync_LeavesUnchangedTargetsAlone_AndRollbackKeepsThem()
	{
		using var fixture = new Fixture();
		var unchangedSource = fixture.WriteSource("9ba626afa44a3aa3.patch_0", "same-payload");
		var replacedSource = fixture.WriteSource("9ba626afa44a3aa3.patch_1", "new-payload");

		// 已部署的两份：patch_0 已经是源文件的硬链接（本次不会变），patch_1 是旧内容（本次会被替换）。
		var unchangedTarget = Path.Combine(fixture.GameData, "9ba626afa44a3aa3.patch_0");
		Assert.True(TryCreateHardLink(unchangedTarget, unchangedSource), "无法在测试环境创建硬链接");
		var replacedTarget = Path.Combine(fixture.GameData, "9ba626afa44a3aa3.patch_1");
		await File.WriteAllTextAsync(replacedTarget, "old-payload");

		var operations = new[]
		{
			Deploy(unchangedTarget, unchangedSource, 0),
			Deploy(replacedTarget, replacedSource, 1),
		};
		var inspection = new DeploymentOwnershipInspection(
			DeploymentOwnershipStatus.Managed,
			fixture.GameData,
			null,
			[],
			[
				Entry(unchangedTarget, unchangedSource, 0),
				Entry(replacedTarget, replacedSource, 1),
			],
			[]);

		await using var transaction = await CliManagedDeploymentTransaction.BeginAsync(inspection, operations);

		// 没变的那个必须原地不动：搬走会让执行器以为它丢了，反而触发全量重建。
		Assert.True(File.Exists(unchangedTarget));
		Assert.Equal("same-payload", await File.ReadAllTextAsync(unchangedTarget));
		// 要替换的那个被搬进事务目录，原位置空出来交给执行器。
		Assert.False(File.Exists(replacedTarget));

		await transaction.RollbackAsync();

		// 回滚只还原它搬走的：没动过的文件不能被回滚删掉。
		Assert.True(File.Exists(unchangedTarget));
		Assert.Equal("same-payload", await File.ReadAllTextAsync(unchangedTarget));
		Assert.True(File.Exists(replacedTarget));
		Assert.Equal("old-payload", await File.ReadAllTextAsync(replacedTarget));
	}

	private static ApplyOperation Deploy(string target, string source, int index)
		=> new(
			ApplyOperationKind.DeployPatch,
			target,
			source,
			"9ba626afa44a3aa3",
			index,
			index,
			PatchSidecarKind.Base,
			ModNodeId.New());

	private static ActivationStateFileEntry Entry(string target, string source, int index)
		=> new(
			target,
			source,
			DeploymentMethod.HardLink,
			"9ba626afa44a3aa3",
			index,
			index,
			PatchSidecarKind.Base,
			ModNodeId.New(),
			12,
			"sha");

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CreateHardLinkW(string linkPath, string existingPath, IntPtr securityAttributes);

	private static bool TryCreateHardLink(string linkPath, string existingPath)
		=> OperatingSystem.IsWindows() && CreateHardLinkW(linkPath, existingPath, IntPtr.Zero);

	private sealed class Fixture : IDisposable
	{
		public Fixture()
		{
			Root = Path.Combine(Path.GetTempPath(), "hd2clitests", Guid.NewGuid().ToString("N"));
			GameData = Path.Combine(Root, "game", "data");
			Directory.CreateDirectory(GameData);
			Directory.CreateDirectory(Path.Combine(Root, "mods", "mod"));
		}

		public string Root { get; }

		public string GameData { get; }

		public string WriteSource(string name, string content)
		{
			var path = Path.Combine(Root, "mods", "mod", name);
			File.WriteAllText(path, content);
			return path;
		}

		public void Dispose()
		{
			try { Directory.Delete(Root, recursive: true); } catch { }
		}
	}
}
