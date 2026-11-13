using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HD2ModCli;

// 作用：把 Patch 源文件的 SHA256 按「路径 + 长度 + 最后写入时间」缓存到磁盘，跨进程复用。
// Purpose: Persists each patch source's SHA256 keyed by path + length + last write time, so repeated
// plan/validate runs — and the two validations inside one apply — stop re-reading every patch.
//
// 为什么需要：部署规划要为每个 Patch 算 SHA256 写进计划。实测 546 个 Patch / 9.3 GB 时单次规划约 84 秒，
// 而 apply 内部会做两次完整校验，等于同一批文件被完整读了两遍。文件没变时这份哈希完全可复用。
//
// 失效判定只看「长度 + 最后写入时间」：Patch 文件由导入器一次性写入、之后只读，
// 长度或时间戳任一变化都会重算。缓存读不出来、写不进去、JSON 坏了都只会退化成"全部重算"，绝不抛给调用方。
internal sealed class CliPatchHashCache
{
	private const string FileName = "patch-hashes.json";
	private const int Version = 1;
	// 超过这个条目数就只保留本次见过的，避免库换过很多轮之后缓存无限增长。
	private const int MaxEntries = 20000;

	private readonly string _path;
	private readonly Dictionary<string, Entry> _entries;
	private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
	private bool _dirty;

	private CliPatchHashCache(string path, Dictionary<string, Entry> entries)
	{
		_path = path;
		_entries = entries;
	}

	public static CliPatchHashCache Load(string? managerRoot)
	{
		var root = string.IsNullOrWhiteSpace(managerRoot) ? Directory.GetCurrentDirectory() : managerRoot;
		var path = Path.Combine(root, "data", "indexes", FileName);
		try
		{
			if (File.Exists(path))
			{
				var document = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(path), JsonOptions);
				if (document is not null && document.Version == Version && document.Entries is not null)
				{
					var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
					foreach (var (key, value) in document.Entries)
					{
						if (value is null || string.IsNullOrWhiteSpace(value.Sha256)) continue;
						entries[key] = value;
					}
					return new CliPatchHashCache(path, entries);
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
		{
			// 缓存坏了就当没有：下一次保存会覆盖成一份好的。
		}

		return new CliPatchHashCache(path, new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase));
	}

	// 命中返回缓存哈希，未命中才真正读文件；命中与否都不抛，读失败交给调用方原有的诊断路径。
	public async ValueTask<string> GetOrComputeAsync(string sourcePath, CancellationToken cancellationToken)
	{
		var fullPath = Path.GetFullPath(sourcePath);
		var info = new FileInfo(fullPath);
		if (!info.Exists)
		{
			throw new FileNotFoundException("部署来源 Patch 不存在。", fullPath);
		}

		_seen.Add(fullPath);
		if (_entries.TryGetValue(fullPath, out var cached)
			&& cached.Length == info.Length
			&& cached.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
		{
			return cached.Sha256;
		}

		await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
		var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
		_entries[fullPath] = new Entry { Length = info.Length, LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks, Sha256 = hash };
		_dirty = true;
		return hash;
	}

	public async ValueTask SaveAsync(CancellationToken cancellationToken)
	{
		if (!_dirty) return;
		try
		{
			var directory = Path.GetDirectoryName(_path);
			if (!string.IsNullOrWhiteSpace(directory))
			{
				Directory.CreateDirectory(directory);
			}

			var keep = _entries.Count > MaxEntries
				? _entries.Where(pair => _seen.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
				: new Dictionary<string, Entry>(_entries, StringComparer.OrdinalIgnoreCase);
			// 文件已经被删掉的条目没有意义，顺手清掉，免得缓存跟着库的改名一路膨胀。
			foreach (var key in keep.Keys.ToArray())
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!File.Exists(key)) keep.Remove(key);
			}

			var document = new CacheDocument { Version = Version, Entries = keep };
			var temporary = _path + ".tmp";
			await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, JsonOptions), cancellationToken).ConfigureAwait(false);
			File.Move(temporary, _path, overwrite: true);
			_dirty = false;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
		{
			// 写不进去不算部署失败：下一次还会重算。
		}
	}

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = false,
	};

	private sealed class CacheDocument
	{
		public int Version { get; set; }
		public Dictionary<string, Entry>? Entries { get; set; }
	}

	private sealed class Entry
	{
		public long Length { get; set; }
		public long LastWriteUtcTicks { get; set; }
		public string Sha256 { get; set; } = string.Empty;
	}
}
