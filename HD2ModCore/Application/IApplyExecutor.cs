using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：执行 ApplyPlan（强制清空旧 patch，按硬链接/软链接/复制顺序部署并验证）。
// Purpose: Executes an ApplyPlan (clears old patches, deploys via hardlink/symlink/copy fallback and verifies).
public interface IApplyExecutor
{
	// contentHasher 可选：给调用方一个机会把 SHA-256 结果缓存起来。硬链接部署时，部署后校验读的
	// 是库里的源文件（和 data 里那份是同一个文件），所以缓存能跨命令复用；不传则每次现算。
	ValueTask<ApplyResult> ExecuteAsync(
		ApplyPlan plan,
		CancellationToken cancellationToken = default,
		Func<string, CancellationToken, ValueTask<string>>? contentHasher = null);
	ValueTask<ApplyResult> DeactivateAsync(string gameDataDirectory, CancellationToken cancellationToken = default);
}
