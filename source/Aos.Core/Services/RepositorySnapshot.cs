using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed class RepositoryPolicyException(string reason) : Exception(reason)
{
    public string ReasonCode { get; } = reason;
}

// Import is bounded and snapshots own their bytes. Tools never read a moving working tree.
public sealed class RepositorySnapshot
{
    public const int MaxFiles = 128;
    public const int MaxFileBytes = 65_536;
    public const int MaxSnapshotBytes = 2_097_152;
    private readonly SortedDictionary<string, string[]> _files;
    public RepositorySnapshotRef Reference { get; }
    public IReadOnlyCollection<string> Paths => _files.Keys;

    private RepositorySnapshot(RepositorySnapshotRef reference, SortedDictionary<string, string[]> files)
    {
        Reference = reference;
        _files = files;
    }

    public static RepositorySnapshot Import(string root, string revision, DateTimeOffset createdAt)
    {
        root = Path.GetFullPath(root);
        RejectLink(root);
        var files = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);
        long total = 0;
        var entriesSeen = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Take(MaxFiles * 2 + 1).Order(StringComparer.Ordinal))
            {
                if (++entriesSeen > MaxFiles * 2) throw new RepositoryPolicyException("repo.snapshot.too_large");
                RejectLink(entry);
                if (Directory.Exists(entry)) { pending.Push(entry); continue; }
                var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                ValidatePath(relative);
                var info = new FileInfo(entry);
                if (info.Length > MaxFileBytes || total + info.Length > MaxSnapshotBytes || files.Count >= MaxFiles)
                    throw new RepositoryPolicyException("repo.snapshot.too_large");
                using var stream = File.OpenRead(entry);
                var bytes = new byte[MaxFileBytes + 1];
                var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                RejectLink(entry);
                if (count > MaxFileBytes || total + count > MaxSnapshotBytes)
                    throw new RepositoryPolicyException("repo.snapshot.too_large");
                total += count;
                string text;
                try { text = new UTF8Encoding(false, true).GetString(bytes, 0, count); }
                catch (DecoderFallbackException) { throw new RepositoryPolicyException("repo.snapshot.not_utf8"); }
                if (text.Contains('\0')) throw new RepositoryPolicyException("repo.snapshot.not_text");
                files.Add(relative, text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'));
                hashes.Add(relative, Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(0, count))));
            }
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(hashes)));
        return new RepositorySnapshot(new RepositorySnapshotRef(
            DiagnosticsSchema.Version, $"sha256:{hash}", hash, revision, createdAt), files);
    }

    public void Verify(RepositorySnapshotRef expected)
    {
        if (expected != Reference) throw new RepositoryPolicyException("repo.snapshot.mismatch");
    }

    public IReadOnlyList<RepositoryLine> Read(string path, int start, int end)
    {
        ValidatePath(path);
        if (start < 1 || end < start || end - start >= 100) throw new RepositoryPolicyException("repo.bounds.invalid");
        if (!_files.TryGetValue(path, out var lines)) throw new RepositoryPolicyException("repo.path.not_found");
        return Enumerable.Range(start, Math.Max(0, Math.Min(end, lines.Length) - start + 1))
            .Select(line => new RepositoryLine(path, line, lines[line - 1])).ToArray();
    }

    public IEnumerable<RepositoryLine> Search(string query, string? glob)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 128) throw new RepositoryPolicyException("repo.query.invalid");
        if (glob is not null)
        {
            ValidatePath(glob.Replace("*", "glob"));
            if (glob.Length > 128 || glob.Count(c => c == '*') > 1 || glob.Contains('?'))
                throw new RepositoryPolicyException("repo.glob.invalid");
        }
        foreach (var (path, lines) in _files)
        {
            if (glob is not null && !MatchesGlob(path, glob)) continue;
            for (var index = 0; index < lines.Length; index++)
                if (lines[index].Contains(query, StringComparison.OrdinalIgnoreCase))
                    yield return new RepositoryLine(path, index + 1, lines[index]);
        }
    }

    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || Path.IsPathRooted(path) ||
            path.Contains('\\') || path.Contains(':') || path.Contains('\0') ||
            path.Split('/').Any(part => part is "" or "." or ".."))
            throw new RepositoryPolicyException("repo.path.denied");
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new RepositoryPolicyException("repo.symlink.denied");
    }

    private static bool MatchesGlob(string path, string glob)
    {
        var index = glob.IndexOf('*');
        return index < 0 ? path == glob : path.StartsWith(glob[..index], StringComparison.Ordinal) &&
            path.EndsWith(glob[(index + 1)..], StringComparison.Ordinal);
    }
}
