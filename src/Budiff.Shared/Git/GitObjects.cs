using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Budiff.Git;

// A repository's objects through two long-running `git cat-file` processes:
// --batch-check answers what an object is (type, size), --batch streams its
// bytes. Reading every file of a change list costs two processes, not two
// per file. Each starts on first use; one instance serves one operation on
// one thread, then is disposed.
internal sealed class GitObjects(string root) : IDisposable
{
    private CatFile? _check;
    private CatFile? _contents;

    public sealed record ObjectInfo(string Sha, string Type, long Size);

    // What a revision expression ("<rev>:<path>", "<ref>^{commit}") names;
    // null when it names nothing.
    public ObjectInfo? Info(string spec)
    {
        if (!Askable(spec))
        {
            return null;
        }

        _check ??= new CatFile(root, "--batch-check");
        return ParseHeader(_check.Ask(spec));
    }

    // The object's first maxBytes bytes (the rest is skipped, not kept);
    // null when the expression names nothing.
    public (ObjectInfo Info, byte[] Bytes)? Read(string spec, long maxBytes = long.MaxValue)
    {
        if (!Askable(spec))
        {
            return null;
        }

        _contents ??= new CatFile(root, "--batch");
        if (ParseHeader(_contents.Ask(spec)) is not { } info)
        {
            return null;
        }

        var kept = (int)Math.Min(info.Size, Math.Min(maxBytes, int.MaxValue));
        var bytes = _contents.ReadBytes(kept);
        _contents.Skip(info.Size - kept + 1);
        return (info, bytes);
    }

    // A ref or revision as a commit sha, branches first (a branch named like
    // a tag or a sha prefix means the branch): HEAD, local, remote, any rev.
    public string? ResolveCommit(string refName)
    {
        if (string.IsNullOrWhiteSpace(refName) || refName.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return Commit("HEAD");
        }

        return Commit("refs/heads/" + refName) ?? Commit("refs/remotes/" + refName) ?? Commit(refName);
    }

    // Many objects in one pass: every expression goes out (from a writer
    // thread) before the answers are read, so a long list costs no round
    // trip per object. Each keeps its first maxBytes bytes; null entries
    // (and expressions that name nothing) answer null.
    public IReadOnlyList<(ObjectInfo Info, byte[] Bytes)?> ReadMany(IReadOnlyList<string?> specs, long maxBytes)
    {
        var asked = specs.Select(s => s is not null && Askable(s)).ToArray();
        var results = new (ObjectInfo, byte[])?[specs.Count];
        if (!asked.Any(a => a))
        {
            return results;
        }

        _contents ??= new CatFile(root, "--batch");
        var contents = _contents;
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < specs.Count; i++)
            {
                if (asked[i])
                {
                    contents.Send(specs[i]!);
                }
            }

            contents.Flush();
        });

        for (var i = 0; i < specs.Count; i++)
        {
            if (asked[i] && ParseHeader(contents.ReadLine()) is { } info)
            {
                var kept = (int)Math.Min(info.Size, Math.Min(maxBytes, int.MaxValue));
                var bytes = contents.ReadBytes(kept);
                contents.Skip(info.Size - kept + 1);
                results[i] = (info, bytes);
            }
        }

        writer.GetAwaiter().GetResult();
        return results;
    }

    // A file's content at a commit, the way the diff wants it (see ToBlob).
    public BlobInfo ReadBlob(string? commit, string path) => ReadBlobs(commit, new[] { path })[0];

    // Several files of one commit (or of none: all empty), in one pass.
    public IReadOnlyList<BlobInfo> ReadBlobs(string? commit, IReadOnlyList<string> paths)
        => ReadMany(paths.Select(p => commit is null ? null : $"{commit}:{p}").ToList(), DiffLoader.MaxInlineDiffBytes)
            .Select(ToBlob)
            .ToList();

    // Text decoded; binary or oversized files sized but not decoded (an
    // oversized one never reaches the inline diff - DiffLoader.Build shows a
    // placeholder - and its start tells binary from text). Empty when the
    // path is not a file there (absent, a folder, a submodule).
    private static BlobInfo ToBlob((ObjectInfo Info, byte[] Bytes)? read)
    {
        if (read is not { Info.Type: "blob" } blob)
        {
            return BlobInfo.Empty;
        }

        if (TextBytes.LooksBinary(blob.Bytes))
        {
            return new BlobInfo(string.Empty, blob.Info.Size, IsBinary: true);
        }

        return blob.Info.Size > DiffLoader.MaxInlineDiffBytes
            ? new BlobInfo(string.Empty, blob.Info.Size, IsBinary: false)
            : new BlobInfo(TextBytes.DecodeUtf8(blob.Bytes), blob.Info.Size, IsBinary: false);
    }

    public void Dispose()
    {
        _check?.Dispose();
        _contents?.Dispose();
    }

    // Through the contents process (a commit is small to skip), so a diff's
    // reads and its ref lookups share one git process.
    private string? Commit(string rev) => Read(rev + "^{commit}", 0) is { Info: { Type: "commit" } info } ? info.Sha : null;

    // The batch protocol is one expression per line.
    private static bool Askable(string spec) => spec.Length > 0 && spec.IndexOfAny(new[] { '\n', '\r' }) < 0;

    // "<sha> <type> <size>", or "<spec> missing" / "<spec> ambiguous".
    private static ObjectInfo? ParseHeader(string header)
    {
        var last = header.LastIndexOf(' ');
        var middle = last > 0 ? header.LastIndexOf(' ', last - 1) : -1;
        if (middle <= 0 || !long.TryParse(header.AsSpan(last + 1), out var size))
        {
            return null;
        }

        return new ObjectInfo(header[..middle], header[(middle + 1)..last], size);
    }

    private sealed class CatFile : IDisposable
    {
        private readonly Process _process;
        private readonly StreamWriter _input;
        private readonly Stream _output;
        private readonly byte[] _scratch = new byte[64 * 1024];

        public CatFile(string root, string mode)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            };
            psi.ArgumentList.Add("cat-file");
            psi.ArgumentList.Add(mode);
            _process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");

            // Drained so a chatty stderr can never block the process.
            _process.ErrorDataReceived += (_, _) => { };
            _process.BeginErrorReadLine();
            _input = _process.StandardInput;
            _output = new BufferedStream(_process.StandardOutput.BaseStream, 64 * 1024);
        }

        public string Ask(string spec)
        {
            Send(spec);
            Flush();
            return ReadLine();
        }

        public void Send(string spec)
        {
            _input.Write(spec);
            _input.Write('\n');
        }

        public void Flush() => _input.Flush();

        public byte[] ReadBytes(int count)
        {
            var bytes = new byte[count];
            var read = 0;
            while (read < count)
            {
                var n = _output.Read(bytes, read, count - read);
                if (n <= 0)
                {
                    throw new IOException("git cat-file stopped answering.");
                }

                read += n;
            }

            return bytes;
        }

        public void Skip(long count)
        {
            while (count > 0)
            {
                var n = _output.Read(_scratch, 0, (int)Math.Min(count, _scratch.Length));
                if (n <= 0)
                {
                    throw new IOException("git cat-file stopped answering.");
                }

                count -= n;
            }
        }

        public void Dispose()
        {
            try
            {
                _input.Close();
                if (!_process.WaitForExit(2_000))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort: it may have exited already.
            }

            _process.Dispose();
        }

        public string ReadLine()
        {
            var line = new MemoryStream();
            int b;
            while ((b = _output.ReadByte()) >= 0 && b != '\n')
            {
                line.WriteByte((byte)b);
            }

            if (b < 0)
            {
                throw new IOException("git cat-file stopped answering.");
            }

            return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
        }
    }
}
