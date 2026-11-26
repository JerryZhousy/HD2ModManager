using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliLibrarySynchronizationExecutionResult(
	bool Success,
	bool Changed,
	IReadOnlyList<string> AddedNodeIds,
	IReadOnlyList<string> ChangedNodeIds,
	IReadOnlyList<string> MissingNodeIds,
	IReadOnlyList<CliLibrarySynchronizationDiagnostic> Diagnostics);

/// <summary>
/// Saves only validated node facts under the ordinary CLI library lease. It
/// never rewrites profiles.json, activation files, physical Mod files, caches,
/// or GameData.
/// </summary>
internal static class CliLibrarySynchronizationExecutor
{
	public static async ValueTask<CliLibrarySynchronizationExecutionResult> ApplyDirectAsync(
		CliWorkspace workspace,
		bool fastMode = true,
		CancellationToken cancellationToken = default)
	{
		var build = await CliLibrarySynchronizationPlanArtifacts
			.BuildAsync(workspace, plannedAddedNodeIdsByRelativePath: null, fastMode: fastMode, cancellationToken)
			.ConfigureAwait(false);
		if (!build.Document.IsReady)
			return Failure(build.Document.Inspection.Diagnostics);

		await using var session = await CliWriteTargetGuard
			.AcquireSessionAsync(workspace, CliWriteTargetMode.Mutation, cancellationToken)
			.ConfigureAwait(false);

		var migration = await CoreServices.CreateModContentLayoutMigration()
			.MigrateAsync(workspace.LibraryRoot!, build.Synchronization.Snapshot, cancellationToken)
			.ConfigureAwait(false);
		if (!migration.IsClean)
		{
			var diagnostics = migration.Conflicts
				.Select(name => new CliLibrarySynchronizationDiagnostic(
					"LibraryLayoutMigrationConflict", "warning", false,
					$"{name} 的新旧两个基底目录（sectional/ 与 vpk2patch/）同时存在；已跳过改名，需要人工确认哪个才是当前产物。",
					Path: name))
				.Concat(migration.Failures.Select(failure => new CliLibrarySynchronizationDiagnostic(
					"LibraryLayoutMigrationFailed", "warning", false,
					$"基底目录改名失败：{failure}")))
				.ToArray();
			if (diagnostics.Length != 0)
				return new CliLibrarySynchronizationExecutionResult(
					true, false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), diagnostics);
		}

		var currentBuild = migration.Renamed > 0
			? await CliLibrarySynchronizationPlanArtifacts.BuildAsync(workspace, plannedAddedNodeIdsByRelativePath: null, fastMode: fastMode, cancellationToken).ConfigureAwait(false)
			: build;

		var synchronization = currentBuild.Synchronization;
		if (!synchronization.HasSnapshotChanges)
		{
			return new CliLibrarySynchronizationExecutionResult(
				true,
				false,
				Array.Empty<string>(),
				Array.Empty<string>(),
				synchronization.MissingNodeIds.OrderBy(id => id.Value).Select(CliProjections.FormatId).ToArray(),
				currentBuild.Document.Inspection.Diagnostics);
		}

		var persisted = await session.SaveNodesOnlyAsync(synchronization.Snapshot, cancellationToken).ConfigureAwait(false);
		var cleanup = CoreServices.CreateModLibraryResidualCleanupService()
			.Cleanup(persisted, workspace.LibraryRoot!, cancellationToken);
		return new CliLibrarySynchronizationExecutionResult(
			true,
			true,
			synchronization.AddedNodeIds.OrderBy(id => id.Value).Select(CliProjections.FormatId).ToArray(),
			synchronization.ChangedNodeIds.OrderBy(id => id.Value).Select(CliProjections.FormatId).ToArray(),
			synchronization.MissingNodeIds.OrderBy(id => id.Value).Select(CliProjections.FormatId).ToArray(),
			currentBuild.Document.Inspection.Diagnostics);
	}

	public static async ValueTask<CliLibrarySynchronizationExecutionResult> ApplyAsync(
		CliLibrarySynchronizationPlanDocument document,
		CliWorkspace workspace,
		bool fastMode = true,
		CancellationToken cancellationToken = default)
	{
		var validation = await CliLibrarySynchronizationPlanArtifacts
			.ValidateAsync(document, workspace, fastMode: fastMode, cancellationToken)
			.ConfigureAwait(false);
		if (!validation.IsValid || validation.Current is null)
			return Failure(validation.Diagnostics);

		await using var session = await CliWriteTargetGuard
			.AcquireSessionAsync(workspace, CliWriteTargetMode.Mutation, cancellationToken)
			.ConfigureAwait(false);
		// 基底目录改名（vpk2patch/ → sectional/）。放在租约内、第二次校验之前：
		// 改名会动磁盘，而下面那次扫描必须看到改名之后的布局。
		var migration = await CoreServices.CreateModContentLayoutMigration()
			.MigrateAsync(workspace.LibraryRoot!, validation.Current.Synchronization.Snapshot, cancellationToken)
			.ConfigureAwait(false);
		if (!migration.IsClean)
		{
			var diagnostics = migration.Conflicts
				.Select(name => new CliLibrarySynchronizationDiagnostic(
					"LibraryLayoutMigrationConflict", "warning", false,
					$"{name} 的新旧两个基底目录（sectional/ 与 vpk2patch/）同时存在；已跳过改名，需要人工确认哪个才是当前产物。",
					Path: name))
				.Concat(migration.Failures.Select(failure => new CliLibrarySynchronizationDiagnostic(
					"LibraryLayoutMigrationFailed", "warning", false,
					$"基底目录改名失败：{failure}")))
				.ToArray();
			if (diagnostics.Length != 0)
				return new CliLibrarySynchronizationExecutionResult(
					true, false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), diagnostics);
		}
		// 若基底目录发生了改名，必须重新扫描以感知新路径；否则安全复用已验证的构建快照。
		var locked = migration.Renamed > 0
			? await CliLibrarySynchronizationPlanArtifacts.ValidateAsync(document, workspace, fastMode: fastMode, cancellationToken).ConfigureAwait(false)
			: validation;
		if (!locked.IsValid || locked.Current is null)
			return Failure(locked.Diagnostics);

		var synchronization = locked.Current.Synchronization;
		if (!synchronization.HasSnapshotChanges)
		{
			return new CliLibrarySynchronizationExecutionResult(
				true,
				false,
				Array.Empty<string>(),
				Array.Empty<string>(),
				synchronization.MissingNodeIds.OrderBy(id => id.Value).Select(CliProjections.FormatId).ToArray(),
				locked.Diagnostics);
		}

		var persisted = await session.SaveNodesOnlyAsync(synchronization.Snapshot, cancellationToken).ConfigureAwait(false);

		// 与 GUI 库同步、与折并走同一份残留清理：节点消失之后，它们留下的空壳该一起收掉。
		var cleanup = CoreServices.CreateModLibraryResidualCleanupService()
			.Cleanup(persisted, workspace.LibraryRoot!, cancellationToken);
		return new CliLibrarySynchronizationExecutionResult(
			true,
			true,
			synchronization.AddedNodeIds.OrderBy(id => id.Value).Select(CliProjections.FormatId).ToArray(),
			synchronization.ChangedNodeIds.OrderBy(id => id.Value).Select(CliProjections.FormatId).ToArray(),
			synchronization.MissingNodeIds.OrderBy(id => id.Value).Select(CliProjections.FormatId).ToArray(),
			locked.Diagnostics);
	}

	private static CliLibrarySynchronizationExecutionResult Failure(
		IReadOnlyList<CliLibrarySynchronizationDiagnostic> diagnostics)
		=> new(false, false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), diagnostics);
}
