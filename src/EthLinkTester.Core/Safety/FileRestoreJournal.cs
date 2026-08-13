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
/// A trailing partial line is expected, not exceptional - it is what a crash mid-append leaves
/// behind. Unparseable lines are skipped so one truncated record cannot make the whole journal
/// unreadable and strand every other entry.
/// </para>
/// </remarks>
public sealed class FileRestoreJournal : IRestoreJournal, IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public FileRestoreJournal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>The journal's location, for recovery messages.</summary>
    public string Path => _path;

    public async Task RecordAsync(PendingRestore entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var line = JsonSerializer.Serialize(entry, RestoreJournalJsonContext.Default.PendingRestore) + '\n';
        var bytes = Encoding.UTF8.GetBytes(line);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var stream = new FileStream(
                _path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough | FileOptions.Asynchronous);

            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PendingRestore>> ReadPendingAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var entries = new List<PendingRestore>();

            foreach (var line in await File.ReadAllLinesAsync(_path, cancellationToken).ConfigureAwait(false))
            {
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
                }
                catch (JsonException)
                {
                    // A truncated final line is what a crash mid-append looks like. Skipping it
                    // keeps every intact entry recoverable.
                }
            }

            return entries;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }
}
