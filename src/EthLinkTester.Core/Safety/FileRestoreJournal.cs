using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EthLinkTester.Core.Safety;

/// <summary>
/// A write-ahead restore journal backed by an append-only file.
/// </summary>
/// <remarks>
/// <para>
/// One JSON object per line. Append-only is the point: a read-modify-write of a single JSON
/// document would have a window where the file is truncated and the previous entries are gone,
/// which is precisely the moment a crash would be unrecoverable.
/// </para>
/// <para>
/// Writes use <see cref="FileOptions.WriteThrough"/> so the bytes reach the disk rather than the
/// operating system's cache before the call returns. Without it a crash could lose entries the
/// caller was told were durable, and the caller has already changed the adapter by then.
/// </para>
/// <para>
/// A trailing partial line is expected, not exceptional - it is what a torn write leaves behind.
/// Unparseable lines are skipped on read and <b>repaired before the next append</b>. That second
/// half is not optional: appending after an unterminated line glues the new record onto the
/// fragment, so one torn write would otherwise make every subsequent entry unreadable too, and a
/// journal in which nothing parses is indistinguishable from an empty one - which is how a forced
/// adapter ends up with no record of how to put it back and no warning that anything is wrong.
/// </para>
/// <para>
/// Every operation runs synchronously inside <see cref="Task.Run(Action)"/> while holding a named
/// mutex. The mutex is what makes a second instance safe - without it, one instance's startup
/// recovery reads, restores, and removes another's live journal mid-run. It must be acquired and
/// released on one thread, which is why nothing here awaits inside the lock: a
/// <see cref="Mutex"/> has thread affinity and an <c>await</c> that resumed elsewhere would fail
/// to release it.
/// </para>
/// </remarks>
public sealed class FileRestoreJournal : IRestoreJournal, IDisposable
{
    private readonly string _path;
    private readonly Mutex _mutex;
    private bool _disposed;

    public FileRestoreJournal(string path, string? mutexName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;

        // Session-local rather than Global: the case that matters is two instances run by the
        // same user, and a Global mutex needs privileges that would make the journal untestable
        // from an unelevated test run.
        _mutex = new Mutex(initiallyOwned: false, mutexName ?? DefaultMutexName(path));
    }

    /// <summary>The journal's location, for recovery messages.</summary>
    public string Path => _path;

    public Task RecordAsync(PendingRestore entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return WithLockAsync(
            () =>
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                RepairUnterminatedTail();
                Append(Serialize(entry));
            },
            cancellationToken);
    }

    public Task<JournalContents> ReadPendingAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return WithLockAsync(() => Read(cancellationToken), cancellationToken);
    }

    public Task RemoveAsync(
        IReadOnlyList<PendingRestore> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (entries.Count == 0)
        {
            return Task.CompletedTask;
        }

        return WithLockAsync(
            () =>
            {
                // Re-read inside the lock rather than trusting what the caller was handed. A
                // restore pass takes seconds, and anything recorded during it must survive: the
                // adapter it describes has already been changed.
                var remaining = Read(cancellationToken).Entries
                    .Where(e => !entries.Contains(e))
                    .ToList();

                if (remaining.Count == 0)
                {
                    Delete();
                    return;
                }

                Rewrite(remaining);
            },
            cancellationToken);
    }

    public Task DiscardAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return WithLockAsync(Delete, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mutex.Dispose();
    }

    /// <summary>
    /// A mutex name derived from the journal's full path, so two instances pointed at the same
    /// file share a lock and two pointed at different files do not contend.
    /// </summary>
    private static string DefaultMutexName(string path) =>
        "EthLinkTester.RestoreJournal." +
        Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).ToUpperInvariant())))[..32];

    private static byte[] Serialize(PendingRestore entry) =>
        Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(entry, RestoreJournalJsonContext.Default.PendingRestore) + '\n');

    private void Append(byte[] bytes)
    {
        using var stream = new FileStream(
            _path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.WriteThrough);

        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private JournalContents Read(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return JournalContents.Empty;
        }

        var entries = new List<PendingRestore>();
        var unreadable = 0;

        using var reader = new StreamReader(
            new FileStream(
                _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
            Encoding.UTF8);

        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize(
                    line, RestoreJournalJsonContext.Default.PendingRestore);

                if (entry is not null)
                {
                    entries.Add(entry);
                }
                else
                {
                    unreadable++;
                }
            }
            catch (JsonException)
            {
                // A torn write leaves a partial line. Skipping it keeps every intact entry
                // recoverable; counting it is what lets the caller tell "nothing was recorded"
                // from "nothing survived", which demand opposite responses.
                unreadable++;
            }
        }

        return new JournalContents { Entries = entries, UnreadableLines = unreadable };
    }

    /// <summary>
    /// Drops a trailing partial line so the next append starts on a fresh record.
    /// </summary>
    /// <remarks>
    /// A line without a terminator can only be a record whose write did not complete. Because the
    /// value is journaled before the adapter is changed, an incomplete record describes a change
    /// that had not yet been applied - so discarding it loses nothing that was ever true, while
    /// keeping it would corrupt every record appended after it.
    /// </remarks>
    private void RepairUnterminatedTail()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        using var stream = new FileStream(
            _path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        if (stream.Length == 0)
        {
            return;
        }

        stream.Seek(-1, SeekOrigin.End);
        if (stream.ReadByte() == '\n')
        {
            return;
        }

        var buffer = new byte[stream.Length];
        stream.Seek(0, SeekOrigin.Begin);
        stream.ReadExactly(buffer);

        // Cut back to the end of the last complete record. No newline at all means the whole file
        // is one partial record, and truncating to zero is correct.
        stream.SetLength(Array.LastIndexOf(buffer, (byte)'\n') + 1);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Replaces the journal's contents, via a sibling file so a crash mid-rewrite leaves either
    /// the old journal or the new one - never a half-written file, which is the failure this whole
    /// class exists to survive.
    /// </summary>
    private void Rewrite(IReadOnlyList<PendingRestore> entries)
    {
        var temporary = _path + ".tmp";

        using (var stream = new FileStream(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough))
        {
            foreach (var entry in entries)
            {
                stream.Write(Serialize(entry));
            }

            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, _path, overwrite: true);
    }

    private void Delete()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    /// <summary>
    /// Runs an operation on a single thread holding the cross-process lock.
    /// </summary>
    /// <remarks>
    /// Synchronous by necessity. A <see cref="Mutex"/> is owned by the thread that took it, and an
    /// <c>await</c> inside the critical section can resume on a different thread-pool thread,
    /// making the release throw. Journal records are a few hundred bytes, so doing the file work
    /// synchronously on a pool thread costs nothing worth having.
    /// <para>
    /// An abandoned mutex means another process died holding it - exactly the crash this journal
    /// is built for. Ownership transfers and the work proceeds: the file is append-only and every
    /// reader tolerates a torn final line, so there is nothing to roll back.
    /// </para>
    /// </remarks>
    private Task<T> WithLockAsync<T>(Func<T> operation, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                try
                {
                    _mutex.WaitOne();
                }
                catch (AbandonedMutexException)
                {
                }

                try
                {
                    return operation();
                }
                finally
                {
                    _mutex.ReleaseMutex();
                }
            },
            cancellationToken);

    private Task WithLockAsync(Action operation, CancellationToken cancellationToken) =>
        WithLockAsync<object?>(
            () =>
            {
                operation();
                return null;
            },
            cancellationToken);
}
