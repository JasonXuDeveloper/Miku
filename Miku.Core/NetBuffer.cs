using System;
using System.Buffers;
using System.Threading;

public class NetBuffer : IDisposable
{
    private readonly byte[] _buffer;
    private int _head; // next write position (producer)
    private int _tail; // next read  position (consumer)
    private bool _disposed;

    public event Action<string> OnWarning;

    public NetBuffer(int capacity = 8192)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = ArrayPool<byte>.Shared.Rent(capacity);
        _head = 0;
        _tail = 0;
    }

    // How many bytes are available to read.
    public int Length
    {
        get
        {
            int head = Volatile.Read(ref _head);
            int tail = Volatile.Read(ref _tail);
            return head >= tail
                ? head - tail
                : head + _buffer.Length - tail;
        }
    }

    // How many bytes we can write without overwriting unread data.
    public int FreeSpace
    {
        get
        {
            int head = Volatile.Read(ref _head);
            int tail = Volatile.Read(ref _tail);
            // leave one slot empty so head==tail always means "empty"
            return tail > head
                ? tail - head - 1
                : tail + _buffer.Length - head - 1;
        }
    }

    // Total capacity of the buffer
    public int Capacity => _buffer.Length;

    public void Clear()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NetBuffer));
        Volatile.Write(ref _head, 0);
        Volatile.Write(ref _tail, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        ArrayPool<byte>.Shared.Return(_buffer);
        _disposed = true;
    }

    // --- PRODUCER SIDE (only one thread may call these) ---

    public MultiMemory<byte> GetWriteSegments()
    {
        if (_disposed)
            return MultiMemory<byte>.Empty;

        int head = Volatile.Read(ref _head);
        int free = FreeSpace;
        if (free == 0)
            return MultiMemory<byte>.Empty;

        // can write up to end of array, then wrap
        int firstLen = Math.Min(free, _buffer.Length - head);
        int secondLen = free - firstLen;

        var first = new Memory<byte>(_buffer, head, firstLen);
        var second = secondLen > 0
            ? new Memory<byte>(_buffer, 0, secondLen)
            : Memory<byte>.Empty;

        return new MultiMemory<byte>(first, second);
    }

    /// <summary>
    /// Get write segments as ArraySegment<byte> for direct SAEA BufferList usage
    /// This avoids MemoryMarshal overhead and provides zero-alloc access
    /// </summary>
    public (ArraySegment<byte> First, ArraySegment<byte> Second) GetWriteSegmentsAsArraySegments()
    {
        if (_disposed)
            return (ArraySegment<byte>.Empty, ArraySegment<byte>.Empty);

        int head = Volatile.Read(ref _head);
        int free = FreeSpace;
        if (free == 0)
            return (ArraySegment<byte>.Empty, ArraySegment<byte>.Empty);

        // can write up to end of array, then wrap
        int firstLen = Math.Min(free, _buffer.Length - head);
        int secondLen = free - firstLen;

        var first = new ArraySegment<byte>(_buffer, head, firstLen);
        var second = secondLen > 0
            ? new ArraySegment<byte>(_buffer, 0, secondLen)
            : ArraySegment<byte>.Empty;

        return (first, second);
    }

    public void AdvanceWrite(int count)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NetBuffer));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count cannot be negative");

        int free = FreeSpace;

        // overwrite old data?
        if (count > free)
        {
            OnWarning?.Invoke(
                $"Buffer full: dropping entire incoming chunk of {count} bytes.");
            return;
        }

        // advance head
        int head = Volatile.Read(ref _head);
        int newHead = head + count;
        if (newHead >= _buffer.Length) newHead -= _buffer.Length;
        Volatile.Write(ref _head, newHead);
    }

    // --- CONSUMER SIDE (only one thread may call these) ---

    public MultiMemory<byte> GetReadSegments()
    {
        if (_disposed)
            return MultiMemory<byte>.Empty;

        int head = Volatile.Read(ref _head);
        int tail = Volatile.Read(ref _tail);
        int len = head >= tail
            ? head - tail
            : head + _buffer.Length - tail;

        if (len == 0)
            return MultiMemory<byte>.Empty;

        int firstLen = Math.Min(len, _buffer.Length - tail);
        int secondLen = len - firstLen;

        var first = new Memory<byte>(_buffer, tail, firstLen);
        var second = secondLen > 0
            ? new Memory<byte>(_buffer, 0, secondLen)
            : Memory<byte>.Empty;

        return new MultiMemory<byte>(first, second);
    }

    public void AdvanceRead(int count)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NetBuffer));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

        int head = Volatile.Read(ref _head);
        int tail = Volatile.Read(ref _tail);
        int len = head >= tail
            ? head - tail
            : head + _buffer.Length - tail;

        if (count > len)
        {
            OnWarning?.Invoke(
                $"Requested to read {count}, but only {len} available. Clearing buffer.");
            // drop all
            Volatile.Write(ref _tail, head);
            return;
        }

        int newTail = tail + count;
        if (newTail >= _buffer.Length) newTail -= _buffer.Length;
        Volatile.Write(ref _tail, newTail);
    }
}