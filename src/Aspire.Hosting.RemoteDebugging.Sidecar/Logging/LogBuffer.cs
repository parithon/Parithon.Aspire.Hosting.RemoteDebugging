namespace Aspire.Hosting.RemoteDebugging.Sidecar.Logging;

/// <summary>
/// Thread-safe circular log buffer that retains the most recent lines and tracks
/// a monotonically increasing byte offset so consumers can resume streaming after
/// a disconnection without replaying the entire history.
/// </summary>
internal sealed class LogBuffer
{
  private readonly int _capacity;
  private readonly Queue<LogEntry> _entries;
  private readonly object _lock = new();
  private long _byteOffset;
  private TaskCompletionSource _newEntry = new(TaskCreationOptions.RunContinuationsAsynchronously);

  public LogBuffer(int capacity = 1_000)
  {
    _capacity = capacity;
    _entries = new Queue<LogEntry>(capacity);
  }

  /// <summary>The total bytes written since this buffer was created.</summary>
  public long ByteOffset => Interlocked.Read(ref _byteOffset);

  public void Write(string line)
  {
    var bytes = System.Text.Encoding.UTF8.GetByteCount(line) + 1; // +1 for newline
    var offset = Interlocked.Add(ref _byteOffset, bytes);

    TaskCompletionSource toComplete;
    lock (_lock)
    {
      if (_entries.Count >= _capacity)
        _entries.Dequeue();
      _entries.Enqueue(new LogEntry(line, offset - bytes));
      toComplete = _newEntry;
      _newEntry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    toComplete.TrySetResult();
  }

  /// <summary>
  /// Returns all buffered entries whose start offset is &gt;= <paramref name="fromOffset"/>,
  /// plus a task that completes when the next new entry is written.
  /// </summary>
  public (IReadOnlyList<LogEntry> Entries, Task NewEntrySignal) GetFrom(long fromOffset)
  {
    lock (_lock)
    {
      var entries = _entries
        .Where(e => e.Offset >= fromOffset)
        .ToArray();

      return (entries, _newEntry.Task);
    }
  }

  /// <summary>Signals all waiting consumers that no more entries will be written.</summary>
  public void Complete()
  {
    lock (_lock)
      _newEntry.TrySetCanceled();
  }
}

internal readonly record struct LogEntry(string Line, long Offset);
