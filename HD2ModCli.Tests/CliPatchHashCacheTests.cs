using System.Security.Cryptography;
using HD2ModCli;
using Xunit;

namespace HD2ModCli.Tests;

// 作用：验证 Patch 哈希缓存的命中/失效/容错契约——它只允许"少读文件"，不允许算错哈希。
// Purpose: Verifies the patch hash cache only ever saves reads; a hit must never change the hash a real read would give.
public sealed class CliPatchHashCacheTests
{
	[Fact]
	public async Task GetOrComputeAsync_ComputesSha256_AndPersistsEntry()
	{
		using var fixture = new Fixture();
		var file = fixture.WriteFile("9ba626afa44a3aa3.patch_0", "payload");

		var cache = CliPatchHashCache.Load(fixture.ManagerRoot);
		var hash = await cache.GetOrComputeAsync(file, CancellationToken.None);
		await cache.SaveAsync(CancellationToken.None);

		Assert.Equal(ExpectedHash("payload"), hash);
		var cachePath = Path.Combine(fixture.ManagerRoot, "data", "indexes", "patch-hashes.json");
		Assert.True(File.Exists(cachePath));
		using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(cachePath));
		var entries = document.RootElement.GetProperty("entries");
		Assert.True(entries.TryGetProperty(Path.GetFullPath(file), out var entry));
		Assert.Equal(hash, entry.GetProperty("sha256").GetString());
	}

	[Fact]
	public async Task GetOrComputeAsync_ReusesPersistedHash_WithoutReadingTheFileAgain()
	{
		using var fixture = new Fixture();
		var file = fixture.WriteFile("9ba626afa44a3aa3.patch_0", "payload");

		var first = CliPatchHashCache.Load(fixture.ManagerRoot);
		var firstHash = await first.GetOrComputeAsync(file, CancellationToken.None);
		await first.SaveAsync(CancellationToken.None);

		// 把内容换成同样长度的另一份，并把时间戳改回去：真正的读盘会得到不同的哈希，
		// 只有走了缓存才会返回旧值。这正好是"命中"的可观测证据。
		var original = new FileInfo(file);
		var lastWrite = original.LastWriteTimeUtc;
		await File.WriteAllTextAsync(file, "PAYLOAD");
		File.SetLastWriteTimeUtc(file, lastWrite);

		var second = CliPatchHashCache.Load(fixture.ManagerRoot);
		var secondHash = await second.GetOrComputeAsync(file, CancellationToken.None);

		Assert.Equal(firstHash, secondHash);
		Assert.NotEqual(ExpectedHash("PAYLOAD"), secondHash);
	}

	[Fact]
	public async Task GetOrComputeAsync_Recomputes_WhenLengthChanges()
	{
		using var fixture = new Fixture();
		var file = fixture.WriteFile("9ba626afa44a3aa3.patch_0", "payload");

		var cache = CliPatchHashCache.Load(fixture.ManagerRoot);
		var before = await cache.GetOrComputeAsync(file, CancellationToken.None);
		await cache.SaveAsync(CancellationToken.None);

		await File.WriteAllTextAsync(file, "payload-changed");

		var reloaded = CliPatchHashCache.Load(fixture.ManagerRoot);
		var after = await reloaded.GetOrComputeAsync(file, CancellationToken.None);

		Assert.NotEqual(before, after);
		Assert.Equal(ExpectedHash("payload-changed"), after);
	}

	[Fact]
	public async Task Load_WithCorruptCacheFile_FallsBackToComputing()
	{
		using var fixture = new Fixture();
		var file = fixture.WriteFile("9ba626afa44a3aa3.patch_0", "payload");
		var cachePath = Path.Combine(fixture.ManagerRoot, "data", "indexes", "patch-hashes.json");
		Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
		await File.WriteAllTextAsync(cachePath, "{ this is not json");

		var cache = CliPatchHashCache.Load(fixture.ManagerRoot);
		var hash = await cache.GetOrComputeAsync(file, CancellationToken.None);

		Assert.Equal(ExpectedHash("payload"), hash);
	}

	private static string ExpectedHash(string content)
		=> Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

	private sealed class Fixture : IDisposable
	{
		public Fixture()
		{
			ManagerRoot = Path.Combine(Path.GetTempPath(), "hd2clitests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(ManagerRoot);
		}

		public string ManagerRoot { get; }

		public string WriteFile(string name, string content)
		{
			var path = Path.Combine(ManagerRoot, "mods", "mod", name);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, content);
			return path;
		}

		public void Dispose()
		{
			try { Directory.Delete(ManagerRoot, recursive: true); } catch { }
		}
	}
}
