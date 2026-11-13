using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// Purpose: Pure read-only inspection of whether top-level GameData patches are provably managed by this manager.
public interface IDeploymentOwnershipInspector
{
	// contentHasher 可选：给调用方一个机会把 SHA-256 结果缓存起来。硬链接部署时校验读的是库里的源文件
	// （data 里那份和它是同一个文件），所以缓存能跨命令复用；不传则每次现算，行为与以前一致。
	ValueTask<DeploymentOwnershipInspection> InspectAsync(
		string? gameDataDirectory,
		CancellationToken cancellationToken = default,
		Func<string, CancellationToken, ValueTask<string>>? contentHasher = null);
}
