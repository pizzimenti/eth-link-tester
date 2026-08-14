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
    private volatile bool _disposed;

    /// <summary>
    /// Operations that have been admitted but not yet finished.
    /// </summary>
    /// <remarks>
    /// Reserved before scheduling rather than counted once running. A caller that passed the
    /// disposal check in a public method is not yet holding the mutex, so draining on mutex
    /// ownership alone would let it be scheduled after the handle was disposed.
    /// </remarks>
    private int _inFlight;

    private readonly IJournalLocation? _location;

    public FileRestoreJournal(
        string path, string? mutexName = null, IJournalLocation? location = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _location = location;

        // Session-local rather than Global: the case that matters is two instances run by the
        // same user, and a Global mutex needs privileges that would make the journal untestable
        // from an unelevated test run.
        _mutex = new Mutex(initiallyOwned: false, mutexName ?? DefaultMutexName(path));
    }

    /// <summary>The journal's location, for recovery messages.</summary>
    public string Path => _path;

    /// <summary>Where a rewrite stages its output before replacing the journal.</summary>
    private string TemporaryPath => _path + ".tmp";

    public Task RecordAsync(PendingRestore entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return WithLockAsync(
            () =>
            {
                EnsureDirectory();

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

        // Wait for work already in flight. Disposing underneath it made its ReleaseMutex throw,
        // so a record that had been written durably was reported to the caller as a failed
        // journal write - which skips the adapter change and leaves a phantom entry that alarms
        // the next launch about a run that never happened.
        // Two things must settle: every admitted operation must have finished, and the mutex must
        // be free. Waiting on ownership alone would miss one that has reserved but not yet
        // acquired.
        var deadline = Environment.TickCount64 + (long)DisposeDrainTimeout.TotalMilliseconds;
        while (Volatile.Read(ref _inFlight) > 0 && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(ReservationPollInterval);
        }

        var drained = false;
        try
        {
            drained = Volatile.Read(ref _inFlight) == 0 && _mutex.WaitOne(DisposeDrainTimeout);
            if (drained)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch (AbandonedMutexException)
        {
            drained = true;
            _mutex.ReleaseMutex();
        }

        // Only dispose once nothing is holding it. An operation that outlives the drain still has
        // a ReleaseMutex to run in its finally block, and disposing underneath it turns work that
        // succeeded into a reported failure - the exact bug this drain exists to prevent. Leaving
        // the handle to the finalizer is the lesser cost.
        if (drained)
        {
            _mutex.Dispose();
        }
    }

    private static readonly TimeSpan AcquirePollInterval = TimeSpan.FromMilliseconds(50);

    private const int ReservationPollInterval = 10;

    /// <summary>
    /// How long disposal waits for in-flight work. Bounded because a cross-process holder must not
    /// be able to hang shutdown; an operation that outlives it is left to the abandoned-mutex path.
    /// </summary>
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(5);

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
        var torn = EndsMidRecord();
        var lineNumber = 0;
        var lastLine = 0;

        // Counted first so the final line can be recognised while reading.
        using (var counter = new StreamReader(
            new FileStream(
                _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
            Encoding.UTF8))
        {
            while (counter.ReadLine() is not null)
            {
                lastLine++;
            }
        }

        using var reader = new StreamReader(
            new FileStream(
                _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
            Encoding.UTF8);

        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lineNumber++;

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
                // A record that did not finish writing is not corruption: it is journaled before
                // its adapter change is applied, so an incomplete one describes a change that
                // never happened. Only a complete line that will not parse means an original
                // value is genuinely lost, and only that warrants alarming anyone.
                if (torn && lineNumber == lastLine)
                {
                    continue;
                }

                unreadable++;
            }
        }

        return new JournalContents
        {
            Entries = entries,
            UnreadableLines = unreadable,
            HasTornFinalLine = torn,
        };
    }

    /// <summary>
    /// Creates the journal's directory, restricted to administrators when a location is supplied.
    /// </summary>
    /// <remarks>
    /// The restriction is applied before the first write rather than checked afterwards. Under
    /// %ProgramData% the inherited permissions grant every standard user append, and an
    /// append-only journal that an elevated process applies to hardware is a complete attack with
    /// one crafted line. A location that cannot be secured throws rather than degrading quietly:
    /// an unprotected journal is not a weaker safety net, it is an attack surface.
    /// </remarks>
    private void EnsureDirectory()
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        if (_location is null)
        {
            Directory.CreateDirectory(directory);
            return;
        }

        _location.Secure(directory);
    }

    /// <summary>Whether the file ends without a record terminator.</summary>
    private bool EndsMidRecord()
    {
        using var stream = new FileStream(
            _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (stream.Length == 0)
        {
            return false;
        }

        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() != Terminator;
    }

    /// <summary>The byte that ends a complete record.</summary>
    private const byte Terminator = (byte)'\n';

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

        // Shares as widely as Append does. Demanding exclusivity here turned a tolerated
        // condition - anyone holding the journal open, including the user inspecting the path the
        // recovery message shows them - into a hard IOException on the next record. The mutex
        // provides exclusion; the share mode does not need to.
        using var stream = new FileStream(
            _path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

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
        var temporary = TemporaryPath;

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

    /// <summary>
    /// Removes the journal and any rewrite temporary.
    /// </summary>
    /// <remarks>
    /// The temporary matters: a crash mid-rewrite leaves a sibling holding a full copy of the
    /// journal, and nothing else ever removes it. An unheld one is harmlessly overwritten by the
    /// next rewrite, but anything holding it - antivirus, a backup agent - makes every future
    /// removal fail.
    /// </remarks>
    private void Delete()
    {
        foreach (var path in new[] { _path, TemporaryPath })
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException) when (path == TemporaryPath)
                {
                    // A held temporary must not prevent the journal itself from being cleared.
                }
            }
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
    /// <summary>
    /// Takes the lock, remaining answerable to cancellation while it waits.
    /// </summary>
    /// <remarks>
    /// A bare WaitOne is uncancellable, so a journal blocked behind another process stayed blocked
    /// for as long as that process held it - passing the token to Task.Run only prevents
    /// scheduling, never interrupts a body already running.
    /// </remarks>
    private void Acquire(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (_mutex.WaitOne(AcquirePollInterval))
                {
                    return;
                }
            }
            catch (AbandonedMutexException)
            {
                // Another process died holding it - exactly the crash this journal is built for.
                // Ownership transfers here, and the file needs no rollback: it is append-only and
                // every reader tolerates a torn final line.
                return;
            }
        }
    }

    private Task<T> WithLockAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _inFlight);

        // Re-checked after reserving, which is the point of reserving first: between the public
        // method's check and this line, Dispose can have run to completion.
        if (_disposed)
        {
            Interlocked.Decrement(ref _inFlight);
            throw new ObjectDisposedException(nameof(FileRestoreJournal));
        }

        // The token is deliberately not passed to Task.Run. Doing so would skip the body outright
        // for an already-cancelled token, and the reservation would never be released. Acquire
        // honours the token as its first act instead.
        return Task.Run(() =>
        {
            try
            {
                Acquire(cancellationToken);

                try
                {
                    return operation();
                }
                finally
                {
                    _mutex.ReleaseMutex();
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        });
    }

    private Task<object?> WithLockAsync(Action operation, CancellationToken cancellationToken) =>
        WithLockAsync<object?>(
            () =>
            {
                operation();
                return null;
            },
            cancellationToken);
}
