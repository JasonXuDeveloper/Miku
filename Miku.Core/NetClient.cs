using System;
using System.Buffers;
using System.Threading;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Diagnostics;

namespace Miku.Core
{
    /// <summary>
    /// Performance statistics for NetClient to track optimization effectiveness
    /// </summary>
    public struct NetClientStats
    {
        public long MessagesReceived;
        public long MessagesSent;
        public long BytesReceived;
        public long BytesSent;
        public long OutgoingMessagesDropped; // Only outgoing messages can be dropped (channel full)
        public long ErrorsSkipped; // Errors skipped due to null OnError handler
        public long TempBufferAllocations; // Temp allocations for segmented data

        /// <summary>
        /// Drop rate for outgoing messages only (incoming messages use direct callbacks - no drops)
        /// </summary>
        public readonly double OutgoingDropRate =>
            MessagesSent > 0 ? (double)OutgoingMessagesDropped / (MessagesSent + OutgoingMessagesDropped) : 0;

        public readonly double ErrorSkipRate =>
            ErrorsSkipped > 0 ? (double)ErrorsSkipped / (ErrorsSkipped + MessagesReceived) : 0;

        /// <summary>
        /// Reset all counters to prevent overflow when approaching long.MaxValue
        /// Thread-safe via Interlocked.Exchange.
        /// </summary>
        public void ResetIfNearOverflow()
        {
            const long resetThreshold = (long)(long.MaxValue * 0.9);

            if (MessagesReceived > resetThreshold || MessagesSent > resetThreshold ||
                BytesReceived   > resetThreshold || BytesSent    > resetThreshold)
            {
                Interlocked.Exchange(ref MessagesReceived,        0);
                Interlocked.Exchange(ref MessagesSent,            0);
                Interlocked.Exchange(ref BytesReceived,           0);
                Interlocked.Exchange(ref BytesSent,               0);
                Interlocked.Exchange(ref OutgoingMessagesDropped, 0);
                Interlocked.Exchange(ref ErrorsSkipped,           0);
                Interlocked.Exchange(ref TempBufferAllocations,   0);
            }
        }
    }

    /// <summary>
    /// Raw message data for the outgoing channel with its buffer writer
    /// </summary>
    internal readonly struct RawMessage
    {
        public readonly ArrayBufferWriter<byte> BufferWriter;
        public readonly int Length;

        public RawMessage(ArrayBufferWriter<byte> bufferWriter, int length)
        {
            BufferWriter = bufferWriter;
            Length = length;
        }
    }

    /// <summary>
    /// Configuration options for NetClient performance tuning
    /// </summary>
    public class NetClientConfig
    {
        /// <summary>
        /// Number of messages to process before yielding thread
        /// </summary>
        public int YieldThreshold { get; set; } = 5;

        /// <summary>
        /// Number of messages to batch before updating stats
        /// </summary>
        public int BatchSize { get; set; } = 32;

        /// <summary>
        /// Channel capacity for outgoing messages
        /// </summary>
        public int OutgoingChannelCapacity { get; set; } = 10000;
    }

    /// <summary>
    /// A client for connecting to a remote host with channel-based data flow
    /// </summary>
    public class NetClient : IDisposable
    {
        /// <summary>
        /// Shared pool for ArrayBufferWriter instances used across all clients
        /// </summary>
        private static readonly ConcurrentQueue<ArrayBufferWriter<byte>> BufferPool = new();

        /// <summary>
        /// Default configuration
        /// </summary>
        private static readonly NetClientConfig DefaultConfig = new();

        /// <summary>
        /// Static counter for generating unique client IDs
        /// </summary>
        private static int _nextId = 0;

        /// <summary>
        /// Generate next unique client ID with overflow handling
        /// </summary>
        private static int GetNextId()
        {
            int newId;
            int current;
            do
            {
                current = _nextId;
                newId = current == int.MaxValue ? 0 : current + 1; // Wrap to 0 on overflow
            } while (Interlocked.CompareExchange(ref _nextId, newId, current) != current);

            return newId;
        }

        /// <summary>
        /// Event when the client is connected
        /// </summary>
        public event Action OnConnected;

        /// <summary>
        /// Event when the client is disconnected
        /// </summary>
        public event Action<string> OnDisconnected;

        /// <summary>
        /// Event when data is received (called from Dispatch)
        /// </summary>
        public event Action<ReadOnlyMemory<byte>> OnDataReceived;

        /// <summary>
        /// Event when an error occurred
        /// </summary>
        public event Action<Exception> OnError;

        /// <summary>
        /// Unique identifier for the client
        /// </summary>
        public int Id { get; } = GetNextId();

        /// <summary>
        /// Whether the client is connected
        /// </summary>
        public bool IsConnected
        {
            get
            {
                if (_disposed != 0 || !_isConnected) return false;

                try
                {
                    var socket = _socket; // Capture reference to prevent null reference
                    return socket?.Connected == true;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Remote host IP address
        /// </summary>
        public string Ip { get; private set; }

        public int BufferSize => _bufferSize;

        /// <summary>
        /// Performance statistics for monitoring optimization effectiveness
        /// </summary>
        public NetClientStats Stats => _stats;

        /// <summary>
        /// Manually reset performance statistics
        /// </summary>
        public void ResetStats()
        {
            _stats = default;
        }

        private Socket _socket;
        private bool _isConnected;
        private int _sending;
        private volatile int _bufferSize;

        private SocketAsyncEventArgs _receiveArg;
        private SocketAsyncEventArgs _sendArg;
        private NetBuffer _receiveBuffer;
        private readonly List<INetMiddleware> _middlewares = new();
        private ArrayBufferWriter<byte> _currentBufferWriter;

        private readonly ArraySegment<byte>[]
            _saeaBufferList = new ArraySegment<byte>[2]; // Fixed-size array to avoid allocations

        private Channel<RawMessage> _outgoingChannel;

        private StringBuilder _diagnosticInfoBuilder = new();

        // Pre-allocated exception for performance
        private static readonly InvalidOperationException CachedReceiveBufferNullException =
            new("Receive buffer is null");

        // Bounding the pool prevents it from growing indefinitely, which can cause
        // memory pressure in long-running processes.
        private const int MaxPooledWriters        = 1_024;

        /// <summary>
        /// Get a buffer writer from the pool or create a new one
        /// </summary>
        private static ArrayBufferWriter<byte> GetBufferFromPool() =>
            BufferPool.TryDequeue(out var writer) ? writer : new ArrayBufferWriter<byte>();

        /// <summary>
        /// Return a buffer writer to the pool
        /// </summary>
        private static void ReturnBufferToPool(ArrayBufferWriter<byte> writer)
        {
            if (writer == null) return;
            try
            {
#if NET8_0_OR_GREATER
                writer.ResetWrittenCount();
#else
                writer.Clear();
#endif
                // ConcurrentQueue.Count is O(N), but this is an acceptable trade-off
                // for safety, as it's called less frequently than enqueueing.
                if (BufferPool.Count >= MaxPooledWriters)
                    return;

                BufferPool.Enqueue(writer);
            }
            catch
            {
                // If returning to the pool fails, let it be garbage collected.
            }
        }

        /// <summary>
        /// Resets a buffer writer to be reused.
        /// </summary>
        private static void ResetBuffer(ArrayBufferWriter<byte> writer)
        {
#if NET8_0_OR_GREATER
            writer.ResetWrittenCount();
#else
            writer.Clear();
#endif
        }

        /// <summary>
        /// Whether there are pending messages to send
        /// </summary>
        public bool HasPendingSends
        {
            get
            {
                try
                {
                    return _outgoingChannel?.Reader.TryPeek(out _) == true;
                }
                catch (InvalidOperationException)
                {
                    // Channel completed
                    return false;
                }
            }
        }

        /// <summary>
        /// Count of pending messages to send
        /// </summary>
        public int PendingSendCount
        {
            get
            {
                try
                {
                    return _outgoingChannel?.Reader.Count ?? 0;
                }
                catch (InvalidOperationException)
                {
                    // Channel completed
                    return 0;
                }
            }
        }

        private int _stopping;
        private int _inErrorHandler; // To prevent reentrancy in error handling
        private int _disposed;
        private bool _isZeroByteReceive;

        // Performance tracking
        private NetClientStats _stats;

        // Cached values for optimization
        private int _yieldCounter;
        private int _overflowCheckCounter;
        private int _localMessagesReceived;
        private long _localBytesReceived;
        private const int OverflowCheckFrequency = 10000;

        /// <summary>
        /// Initialize the client with the given socket and buffer size
        /// </summary>
        private void InitializeClient(Socket socket, int bufferSize)
        {
            _bufferSize = bufferSize;
            _socket = socket;

            var remoteEndPoint = socket.RemoteEndPoint as System.Net.IPEndPoint;
            Ip = remoteEndPoint?.Address.ToString() ?? "Unknown";

            _receiveBuffer = new(bufferSize);

            _receiveArg = new SocketAsyncEventArgs();
            _receiveArg.UserToken = this;
            _receiveArg.Completed += HandleReadWrite;

            // _saeaBufferList is already allocated as a field.
            _sendArg = new SocketAsyncEventArgs();
            _sendArg.UserToken = this;
            _sendArg.Completed += HandleReadWrite;

            var outgoingOptions = new BoundedChannelOptions(DefaultConfig.OutgoingChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite, // Drop new messages when the channel is full.
                SingleReader = true,
                SingleWriter = false
            };
            _outgoingChannel = Channel.CreateBounded<RawMessage>(outgoingOptions);

            // Register warning handler after initialization.
            _receiveBuffer.OnWarning += msg => SafeInvokeOnError(new Exception(msg));

            // Set _isConnected only after all initialization is complete.
            _isConnected = true;

            OnConnected?.Invoke();

            // Start the first receive.
            TryStartReceive(_receiveArg);
        }

        /// <summary>
        /// Initiates an asynchronous receive operation with error handling.
        /// </summary>
        private void StartReceiveAsync(SocketAsyncEventArgs args)
        {
            try
            {
                if (!_socket.ReceiveAsync(args))
                {
                    Receive(args);
                }
            }
            catch (ObjectDisposedException)
            {
                // Socket was disposed, so we can ignore this.
            }
            catch (Exception e)
            {
                SafeInvokeOnError(e);
            }
        }

        /// <summary>
        /// Try to start a receive operation, handling buffer space gracefully
        /// </summary>
        private void TryStartReceive(SocketAsyncEventArgs args)
        {
            // Cache state to avoid multiple volatile reads
            var isDisposed = _disposed != 0;
            var isConnected = _isConnected;
            var socket = _socket;
            var receiveBuffer = _receiveBuffer;

            if (isDisposed || !isConnected || socket == null || receiveBuffer == null)
                return;

            // Always process existing buffered data first to prevent deadlocks.
            if (receiveBuffer.Length > 0)
            {
                try
                {
                    ProcessReceivedData();
                }
                catch (Exception e)
                {
                    SafeInvokeOnError(e);
                }
            }

            // Re-check state, as it may have changed.
            if (_disposed != 0 || !_isConnected || _socket == null)
                return;

            if (SetupReceiveBufferList())
            {
                _isZeroByteReceive = false;
                StartReceiveAsync(args);
            }
            else
            {
                // Post a zero-byte receive to detect disconnections.
                _isZeroByteReceive = true;
                args.BufferList = null;
                args.SetBuffer(Array.Empty<byte>(), 0, 0);
                StartReceiveAsync(args);
            }
        }

        /// <summary>
        /// Setup BufferList for receiving with fixed-size array (zero-alloc)
        /// </summary>
        /// <returns>True if buffer space is available, false if no space</returns>
        private bool SetupReceiveBufferList()
        {
            if (_disposed != 0 || !_isConnected || _receiveBuffer == null)
                return false;

            var (first, second) = _receiveBuffer.GetWriteSegmentsAsArraySegments();
            if ((first.Count | second.Count) == 0)
            {
                return false;
            }

            // Update fixed-size array in-place to avoid allocations.
            _saeaBufferList[0] = first;
            _saeaBufferList[1] = second;

            // Use BufferList for the receive operation.
            _receiveArg.SetBuffer(null, 0, 0);
            _receiveArg.BufferList = _saeaBufferList;

            return true;
        }

        /// <summary>
        /// Cleanup resources on connection failure
        /// </summary>
        private void CleanupOnConnectionFailure()
        {
            _isConnected = false;
            _bufferSize = 0;
            _socket?.Close();
            _socket?.Dispose();
            _socket = null;

            _receiveBuffer?.Dispose();
            _receiveBuffer = null;

            _receiveArg?.Dispose();
            _receiveArg = null;

            _sendArg?.Dispose();
            _sendArg = null;

            // _saeaBufferList is a field, no need to clear it.
            _outgoingChannel?.Writer.TryComplete();
            _outgoingChannel = null;
        }

        /// <summary>
        /// Safely invoke OnError without causing reentrancy issues
        /// </summary>
        private void SafeInvokeOnError(Exception exception)
        {
            // Prevent reentrancy - if we're already in an error handler, don't invoke again
            if (Interlocked.CompareExchange(ref _inErrorHandler, 1, 0) == 1)
            {
                return;
            }

            try
            {
                OnError?.Invoke(exception);
            }
            catch
            {
                // Ignore exceptions from error handlers to prevent loops.
            }
            finally
            {
                Interlocked.Exchange(ref _inErrorHandler, 0);
            }
        }

        /// <summary>
        /// Add a middleware to the client
        /// </summary>
        /// <param name="middleware"></param>
        public void AddMiddleware(INetMiddleware middleware)
        {
            _middlewares.Add(middleware);
        }

        /// <summary>
        /// Remove a middleware from the client
        /// </summary>
        /// <param name="middleware"></param>
        public void RemoveMiddleware(INetMiddleware middleware)
        {
            _middlewares.Remove(middleware);
        }

        /// <summary>
        /// Connect to the remote host
        /// </summary>
        /// <param name="ip"></param>
        /// <param name="port"></param>
        /// <param name="bufferSize"></param>
        /// <exception cref="InvalidOperationException"></exception>
        public void Connect(string ip, int port, int bufferSize = 1024)
        {
            if (_disposed != 0)
            {
                throw new ObjectDisposedException(nameof(NetClient));
            }

            if (_isConnected)
            {
                throw new InvalidOperationException("Already connected");
            }

            if (string.IsNullOrWhiteSpace(ip))
            {
                throw new ArgumentException("IP address cannot be null or empty", nameof(ip));
            }

            if (port <= 0 || port > 65535)
            {
                throw new ArgumentException("Port must be between 1 and 65535", nameof(port));
            }

            // This is important on macOS, where sockets can become invalid after failed connections.
            if (_socket != null)
            {
                try
                {
                    _socket.Close();
                    _socket.Dispose();
                }
                catch
                {
                    // Ignore cleanup errors.
                }
                _socket = null;
            }

            try
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                _socket.NoDelay = true;
                _socket.Connect(ip, port);

                InitializeClient(_socket, bufferSize);
                Ip = ip;
            }
            catch (Exception e)
            {
                CleanupOnConnectionFailure();
                SafeInvokeOnError(e);
                throw;
            }
        }

        /// <summary>
        /// When a server accepts a connection, use this method to connect the client
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="bufferSize"></param>
        /// <exception cref="InvalidOperationException"></exception>
        internal void Connect(Socket socket, int bufferSize = 1024)
        {
            if (_disposed != 0)
            {
                throw new ObjectDisposedException(nameof(NetClient));
            }

            if (_isConnected)
            {
                throw new InvalidOperationException("Already connected");
            }

            if (socket == null)
            {
                throw new ArgumentNullException(nameof(socket));
            }

            if (!socket.Connected)
            {
                throw new ArgumentException("Socket must be connected", nameof(socket));
            }

            try
            {
                socket.NoDelay = true;
                InitializeClient(socket, bufferSize);
            }
            catch (Exception e)
            {
                // Clean up, but don't dispose the socket as we didn't create it.
                _isConnected = false;
                _bufferSize = 0;
                _socket = null;

                _receiveBuffer?.Dispose();
                _receiveBuffer = null;

                _receiveArg?.Dispose();
                _receiveArg = null;

                _sendArg?.Dispose();
                _sendArg = null;

                _outgoingChannel?.Writer.TryComplete();
                _outgoingChannel = null;

                SafeInvokeOnError(e);
                throw;
            }
        }

        /// <summary>
        /// Stop the client with a specific reason
        /// </summary>
        /// <param name="reason">Reason for closing the connection</param>
        /// <param name="includeCallStack">Whether to include call stack information (expensive)</param>
        public void Stop(string reason = "Connection closed by client", bool includeCallStack = false)
        {
            string fullReason = reason;

            // Only generate expensive stack trace when requested.
            if (includeCallStack)
            {
                try
                {
                    var stackTrace = new StackTrace(true);
                    var callStack = new List<string>();
                    for (int i = 1; i < Math.Min(stackTrace.FrameCount, 10); i++) // Limit to 10 frames
                    {
                        var frame = stackTrace.GetFrame(i);
                        var method = frame?.GetMethod();
                        if (method != null)
                        {
                            var className = method.DeclaringType?.Name ?? "Unknown";
                            var methodName = method.Name;
                            callStack.Add($"{className}.{methodName}");
                        }
                    }

                    fullReason = $"{reason} (called from {string.Join(" -> ", callStack)})";
                }
                catch
                {
                    // If stack trace fails, just use the original reason.
                }
            }

            StopInternal(fullReason);
        }

        /// <summary>
        /// Drains and cleans up the outgoing message channel.
        /// </summary>
        private void CleanupOutgoingChannel()
        {
            if (_outgoingChannel == null) return;
            try
            {
                // This signals that no more items will be written to the channel.
                _outgoingChannel.Writer.TryComplete();

                // Drain any remaining messages in the channel.
                while (_outgoingChannel.Reader.TryRead(out var outgoingMessage))
                {
                    if (outgoingMessage.BufferWriter != null)
                    {
                        ReturnBufferToPool(outgoingMessage.BufferWriter);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Channel may have been completed and emptied by another thread.
            }
            catch (Exception e)
            {
                // Log other exceptions, as they might indicate a problem.
                SafeInvokeOnError(e);
            }
        }

        /// <summary>
        /// Fast stop for high-frequency scenarios to avoid string allocations.
        /// </summary>
        /// <param name="errorCode">Socket error code or exception type</param>
        /// <param name="context">Context where the error occurred</param>
        public void StopFast(int errorCode, string context)
        {
            var reason = $"{context}: {errorCode}";
            StopInternal(reason);
        }

        /// <summary>
        /// Internal stop implementation to avoid code duplication
        /// </summary>
        private void StopInternal(string fullReason)
        {
            if (Interlocked.CompareExchange(ref _stopping, 1, 0) == 1)
            {
                return;
            }

            try
            {
                if (!_isConnected)
                {
                    CleanupOutgoingChannel();
                    return;
                }

                _isConnected = false;

                try
                {
                    _socket?.Shutdown(SocketShutdown.Both);
                }
                catch (Exception e)
                {
                    SafeInvokeOnError(e);
                }

                _socket?.Close();
                _socket?.Dispose();
                _socket = null;

                // Complete the channel writer before waiting for sends to prevent new messages.
                _outgoingChannel?.Writer.TryComplete();

                // Wait for pending sends to complete.
                if (!SpinWait.SpinUntil(() => _sending == 0, TimeSpan.FromMilliseconds(100)))
                {
                    // Force reset on timeout.
                    Interlocked.Exchange(ref _sending, 0);
                }

                // Clean up the current buffer writer.
                var currentBufferWriter = Interlocked.Exchange(ref _currentBufferWriter, null);
                if (currentBufferWriter != null)
                {
                    ReturnBufferToPool(currentBufferWriter);
                }

                // Clean up remaining messages in channels.
                CleanupOutgoingChannel();

                _receiveArg?.Dispose();
                _sendArg?.Dispose();
                _receiveBuffer?.Dispose();

                _receiveArg = null;
                _sendArg = null;
                _receiveBuffer = null;

                _outgoingChannel = null;

                _bufferSize = 0;

                try
                {
                    // Clear event handlers before invoking OnDisconnected to prevent memory leaks.
                    var onDisconnectedHandler = OnDisconnected;

                    OnConnected = null;
                    OnDisconnected = null;
                    OnDataReceived = null;
                    OnError = null;

                    onDisconnectedHandler?.Invoke(fullReason);
                }
                catch (Exception e)
                {
                    SafeInvokeOnError(e);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _stopping, 0);
            }
        }

        /// <summary>
        /// Send data to the remote host via outgoing channel
        /// </summary>
        /// <param name="data"></param>
        /// <returns>Whether the data was queued successfully</returns>
        public void Send(ReadOnlyMemory<byte> data)
        {
            if (Volatile.Read(ref _disposed) != 0 || !Volatile.Read(ref _isConnected) || Volatile.Read(ref _stopping) != 0)
            {
                return;
            }

            var bufferSize = _bufferSize;
            if (bufferSize == 0)
            {
                return;
            }

            ArrayBufferWriter<byte> buffer1 = GetBufferFromPool();
            ArrayBufferWriter<byte> buffer2 = null;

            ResetBuffer(buffer1);

            var span = buffer1.GetSpan(data.Length);
            data.Span.CopyTo(span);
            buffer1.Advance(data.Length);

            try
            {
                if (_middlewares.Count > 0)
                {
                    buffer2 = GetBufferFromPool();

                    ResetBuffer(buffer2);
                    var src = buffer1;
                    var dst = buffer2;

                    foreach (var middleware in _middlewares)
                    {
                        // Process using current src (input) -> dst (output).
                        middleware.ProcessSend(src.WrittenMemory, dst);

                        // The dst buffer is now the src for the next iteration.
                        (src, dst) = (dst, src);

                        // Clear the new dst buffer.
                        ResetBuffer(dst);
                    }

                    // `src` holds the final data, `dst` is temporary.
                    buffer1 = src;
                    ReturnBufferToPool(dst);
                    buffer2 = null; // Prevent double-return in `finally`.
                }

                data = buffer1.WrittenMemory;

                if (data.Length > bufferSize)
                {
                    SafeInvokeOnError(new ArgumentException(
                        $"Send data too large: {data.Length} bytes (max: {bufferSize})"));
                    return;
                }

                if (data.IsEmpty)
                {
                    return;
                }

                try
                {
                    var rawMessage = new RawMessage(buffer1, data.Length);

                    if (_outgoingChannel?.Writer.TryWrite(rawMessage) != true)
                    {
                        ReturnBufferToPool(buffer1);
                        Interlocked.Increment(ref _stats.OutgoingMessagesDropped);
                        return;
                    }

                    // Don't send if stopping.
                    if (Volatile.Read(ref _stopping) != 0)
                    {
                        return;
                    }

                    // Queue the message and trigger send.
                    Interlocked.Increment(ref _stats.MessagesSent);
                    Interlocked.Add(ref _stats.BytesSent, data.Length);

                    // Check for counter overflow.
                    if (Interlocked.Increment(ref _overflowCheckCounter) >= OverflowCheckFrequency)
                    {
                        Interlocked.Exchange(ref _overflowCheckCounter, 0);
                        _stats.ResetIfNearOverflow();
                    }

                    SendPending();
                }
                catch (Exception e)
                {
                    // Return buffer on exception.
                    if (buffer1 != null)
                    {
                        ReturnBufferToPool(buffer1);
                    }
                    SafeInvokeOnError(e);
                }
            }
            catch (Exception e)
            {
                SafeInvokeOnError(e);
            }
            finally
            {
                if (buffer2 != null) ReturnBufferToPool(buffer2);
            }
        }

        /// <summary>
        /// Send all pending data from the outgoing channel
        /// </summary>
        public void SendPending()
        {
            if (_disposed != 0 || !_isConnected || _stopping != 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _sending, 1, 0) == 1)
            {
                return;
            }

            TrySendNext();
        }

        /// <summary>
        /// Try to send the next message from the outgoing channel
        /// </summary>
        private void TrySendNext()
        {
            if (_disposed != 0 || !_isConnected || _stopping != 0)
            {
                Interlocked.Exchange(ref _sending, 0);
                return;
            }

            if (_outgoingChannel?.Reader.TryRead(out var rawMessage) != true)
            {
                Interlocked.Exchange(ref _sending, 0);
                return;
            }

            SendInternal(rawMessage);
        }

        /// <summary>
        /// Determines if a socket error indicates a normal connection closure rather than an error
        /// </summary>
        private static bool IsConnectionClosedError(SocketError error)
        {
            return error == SocketError.ConnectionAborted ||
                   error == SocketError.ConnectionReset ||
                   error == SocketError.Disconnecting ||
                   error == SocketError.Shutdown ||
                   error == SocketError.NotConnected ||
                   error == SocketError.TimedOut ||
                   error == SocketError.NetworkDown ||
                   error == SocketError.NetworkUnreachable ||
                   error == SocketError.HostDown ||
                   error == SocketError.HostUnreachable;
        }

        /// <summary>
        /// Internal method to actually send preprocessed data over the socket
        /// </summary>
        private void SendInternal(RawMessage rawMessage)
        {
            if (_disposed != 0 || !_isConnected)
            {
                ReturnBufferToPool(rawMessage.BufferWriter);
                Interlocked.Exchange(ref _sending, 0);
                return;
            }

            var bufferSize = _bufferSize;
            if (bufferSize == 0)
            {
                ReturnBufferToPool(rawMessage.BufferWriter);
                Interlocked.Exchange(ref _sending, 0);
                return;
            }

            try
            {
                var writtenMemory = rawMessage.BufferWriter.WrittenMemory.Slice(0, rawMessage.Length);

                _sendArg.SetBuffer(MemoryMarshal.AsMemory(writtenMemory));

                // Store buffer writer for cleanup after send.
                var previousBufferWriter = Interlocked.Exchange(ref _currentBufferWriter, rawMessage.BufferWriter);
                // Clean up previous writer to prevent leaks.
                if (previousBufferWriter != null)
                {
                    ReturnBufferToPool(previousBufferWriter);
                }

                if (!_socket.SendAsync(_sendArg))
                {
                    HandleSendComplete(_sendArg);
                }
            }
            catch (SocketException se)
            {
                ReturnBufferToPool(rawMessage.BufferWriter);

                Interlocked.Exchange(ref _sending, 0);

                // Don't report normal connection closure as an error.
                if (!IsConnectionClosedError(se.SocketErrorCode))
                {
                    SafeInvokeOnError(se);
                }

                StopFast((int)se.SocketErrorCode, "HandleReadWrite SocketException");
            }
            catch (Exception e)
            {
                ReturnBufferToPool(rawMessage.BufferWriter);

                Interlocked.Exchange(ref _sending, 0);
                SafeInvokeOnError(e);
                // Stop with callstack for unexpected exceptions.
                Stop("HandleSendComplete Exception", true);
            }
        }

        private static void HandleReadWrite(object sender, SocketAsyncEventArgs args)
        {
            NetClient client = args.UserToken as NetClient;
            try
            {
                if (client is not { IsConnected: true })
                    return;

                switch (args.LastOperation)
                {
                    case SocketAsyncOperation.Send:
                        client.HandleSendComplete(args);
                        break;
                    case SocketAsyncOperation.Receive:
                        Receive(args);
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown operation: {args.LastOperation}");
                }
            }
            catch (ObjectDisposedException)
            {
                // Ignore if socket is closed.
            }
            catch (SocketException se)
            {
                // Only invoke OnError if it's not a normal connection closure
                if (!IsConnectionClosedError(se.SocketErrorCode))
                {
                    client?.SafeInvokeOnError(se);
                }

                client?.StopFast((int)se.SocketErrorCode, "HandleReadWrite SocketException");
            }
            catch (Exception e)
            {
                client?.SafeInvokeOnError(e);
                client?.Stop("HandleReadWrite Exception", true); // Include call stack for unexpected exceptions
            }
        }

        private void HandleSendComplete(SocketAsyncEventArgs args)
        {
            // Get and clear the current buffer writer.
            ArrayBufferWriter<byte> bufferWriterToReturn = Interlocked.Exchange(ref _currentBufferWriter, null);

            try
            {
                args.SetBuffer(null, 0, 0);

                if (args.SocketError != SocketError.Success)
                {
                    Interlocked.Exchange(ref _sending, 0);

                    // Only invoke OnError if it's not a normal connection closure
                    if (!IsConnectionClosedError(args.SocketError))
                    {
                        SafeInvokeOnError(new SocketException((int)args.SocketError));
                    }

                    StopFast((int)args.SocketError, "HandleSendComplete SocketError");
                    return;
                }

                // Try to send the next message.
                TrySendNext();
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref _sending, 0);
                SafeInvokeOnError(e);
            }
            finally
            {
                // Ensure the buffer is always returned to the pool.
                ReturnBufferToPool(bufferWriterToReturn);
            }
        }

        private static void Receive(SocketAsyncEventArgs args)
        {
            NetClient client = (NetClient)args.UserToken!;

            if (args is { BytesTransferred: > 0, SocketError: SocketError.Success })
            {
                try
                {
                    if (client._receiveBuffer == null)
                    {
                        client.SafeInvokeOnError(CachedReceiveBufferNullException);
                        client.Stop("Receive buffer is null");
                        return;
                    }

                    client._receiveBuffer.AdvanceWrite(args.BytesTransferred);
                    client.ProcessReceivedData();
                }
                catch (Exception e)
                {
                    client.SafeInvokeOnError(e);
                }

                if (!client._isConnected || client._socket == null || client._receiveBuffer == null)
                {
                    return;
                }

                client.TryStartReceive(args);
            }
            else
            {
                if (client._isZeroByteReceive && args.SocketError == SocketError.Success)
                {
                    client.TryStartReceive(args);
                    return;
                }

                if (args.SocketError != SocketError.Success)
                {
                    client.StopFast((int)args.SocketError, "Socket error");
                }
                else if (args.BytesTransferred == 0)
                {
                    // 0 bytes means remote graceful close.
                    var additionalInfo = "";
                    try
                    {
                        // Check socket state for more info.
                        if (client._socket != null)
                        {
                            var socketConnected = client._socket.Connected;
                            var socketAvailable = client._socket.Available;
                            additionalInfo = $" (Socket.Connected={socketConnected}, Available={socketAvailable})";

                            if (socketConnected && socketAvailable == 0)
                            {
                                additionalInfo += " - Appears to be graceful close";
                            }
                            else if (!socketConnected)
                            {
                                additionalInfo += " - Socket already marked as disconnected";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        additionalInfo = $" (Socket check failed: {ex.Message})";
                    }

                    client.Stop($"Remote endpoint closed connection (0 bytes received){additionalInfo}");
                }
                else
                {
                    client.Stop($"Unexpected disconnect (BytesTransferred: {args.BytesTransferred})");
                }
            }
        }

        /// <summary>
        /// Check for complete messages in the receive buffer and directly invoke callbacks
        /// </summary>
        private void ProcessReceivedData()
        {
            if (_disposed != 0 || _receiveBuffer == null)
                return;

            if (_receiveBuffer.Length == 0)
            {
                return;
            }

            var bufferSize = _bufferSize;
            if (bufferSize == 0)
            {
                return;
            }

            var data = _receiveBuffer.GetReadSegments();

            byte[] temp = null;
            ReadOnlyMemory<byte> src = data.First;

            if (!data.Second.IsEmpty)
            {
                // Segmented data, use a temporary buffer.
                var totalLength = data.Length;
                temp = ArrayPool<byte>.Shared.Rent(totalLength);
                Interlocked.Increment(ref _stats.TempBufferAllocations);
                data.CopyTo(temp);
                src = new ReadOnlyMemory<byte>(temp, 0, totalLength);
            }

            try
            {
                int totalConsumedFromBuffer = 0;

                while (!src.IsEmpty)
                {
                    ReadOnlyMemory<byte> receivedData = src;
                    int consumedFromOrigin = src.Length;

                    ArrayBufferWriter<byte> buffer1 = null;
                    if (_middlewares.Count > 0)
                    {
                        consumedFromOrigin = 0;

                        buffer1 = GetBufferFromPool();
                        var buffer2 = GetBufferFromPool();
                        ResetBuffer(buffer1);
                        ResetBuffer(buffer2);
                        var span = buffer1.GetSpan(src.Length);
                        src.Span.CopyTo(span);
                        buffer1.Advance(src.Length);

                        var middlewareSrc = buffer1;
                        var middlewareDst = buffer2;

                        // Process through middleware (reverse order to undo send transformations).
                        for (int i = _middlewares.Count - 1; i >= 0; i--)
                        {
                            try
                            {
                                var (halt, consumed) = _middlewares[i].ProcessReceive(middlewareSrc.WrittenMemory, middlewareDst);
                                // Accumulate consumption from middleware.
                                consumedFromOrigin += consumed;

                                if (halt)
                                {
                                    // Halt processing and advance buffer.
                                    totalConsumedFromBuffer += consumedFromOrigin;
                                    _receiveBuffer.AdvanceRead(totalConsumedFromBuffer);
                                    ReturnBufferToPool(buffer1);
                                    ReturnBufferToPool(buffer2);
                                    return;
                                }

                                // Swap buffers for next middleware.
                                (middlewareDst, middlewareSrc) = (middlewareSrc, middlewareDst);

                                // Clear the destination buffer for next use.
                                ResetBuffer(middlewareDst);
                            }
                            catch (Exception e)
                            {
                                SafeInvokeOnError(e);
                                // Halt and advance buffer on error.
                                totalConsumedFromBuffer += consumedFromOrigin;
                                _receiveBuffer.AdvanceRead(totalConsumedFromBuffer);
                                ReturnBufferToPool(buffer1);
                                ReturnBufferToPool(buffer2);
                                return;
                            }
                        }

                        // `middlewareSrc` has the final data.
                        receivedData = middlewareSrc.WrittenMemory;
                        buffer1 = middlewareSrc;
                        ReturnBufferToPool(middlewareDst);
                    }

                    // No more data, advance buffer and break.
                    if (receivedData.IsEmpty)
                    {
                        totalConsumedFromBuffer += consumedFromOrigin;
                        if (buffer1 != null)
                        {
                            ReturnBufferToPool(buffer1);
                        }
                        break;
                    }

                    // Invoke callback with processed data.
                    try
                    {
                        OnDataReceived?.Invoke(receivedData);
                    }
                    catch (Exception callbackEx)
                    {
                        // Log callback errors, but continue.
                        SafeInvokeOnError(callbackEx);
                    }
                    finally
                    {
                        if (buffer1 != null)
                        {
                            ReturnBufferToPool(buffer1);
                        }

                        // Track stats.
                        _localMessagesReceived++;
                        _localBytesReceived += consumedFromOrigin;

                        // Flush batched stats.
                        if (_localMessagesReceived >= DefaultConfig.BatchSize)
                        {
                            Interlocked.Add(ref _stats.MessagesReceived, _localMessagesReceived);
                            Interlocked.Add(ref _stats.BytesReceived, _localBytesReceived);
                            _localMessagesReceived = 0;
                            _localBytesReceived = 0;
                        }
                    }

                    totalConsumedFromBuffer += consumedFromOrigin;

                    if (consumedFromOrigin >= src.Length)
                    {
                        break;
                    }
                    src = src.Slice(consumedFromOrigin);

                    // Use SpinWait instead of Thread.Yield for better performance.
                    if (++_yieldCounter >= DefaultConfig.YieldThreshold)
                    {
                        _yieldCounter = 0;
                        Thread.Yield();
                    }

                    // Check for counter overflow.
                    if (Interlocked.Increment(ref _overflowCheckCounter) >= OverflowCheckFrequency)
                    {
                        Interlocked.Exchange(ref _overflowCheckCounter, 0);
                        _stats.ResetIfNearOverflow();
                    }
                }

                // Advance read position.
                if (totalConsumedFromBuffer > 0)
                {
                    _receiveBuffer.AdvanceRead(totalConsumedFromBuffer);
                }
            }
            finally
            {
                // Return temp buffer to pool.
                if (temp != null)
                {
                    ArrayPool<byte>.Shared.Return(temp);
                }

                // Flush remaining batched stats.
                if (_localMessagesReceived > 0)
                {
                    Interlocked.Add(ref _stats.MessagesReceived, _localMessagesReceived);
                    Interlocked.Add(ref _stats.BytesReceived, _localBytesReceived);
                    _localMessagesReceived = 0;
                    _localBytesReceived = 0;
                }
            }
        }

        /// <summary>
        /// Get detailed status information for debugging data loss issues
        /// </summary>
        public string GetDiagnosticInfo()
        {
            if (_disposed != 0)
                return "NetClient disposed";

            // Cache values for performance.
            var stats = _stats;
            var isConnected = IsConnected;
            var hasPendingSends = HasPendingSends;
            var bufferLength = _receiveBuffer?.Length ?? 0;
            var bufferFreeSpace = _receiveBuffer?.FreeSpace ?? 0;
            var bufferCapacity = _receiveBuffer?.Capacity ?? 0;
            var isZeroByteReceive = _isZeroByteReceive;
            var isSending = _sending != 0;

            _diagnosticInfoBuilder.Clear();

            _diagnosticInfoBuilder
                .Append("NetClient ").Append(Id).AppendLine(" Diagnostics:")
                .Append("  Connected: ").AppendLine(isConnected ? "True" : "False")
                .Append("  Messages Received: ").AppendLine(stats.MessagesReceived.ToString())
                .Append("  Outgoing Messages Dropped: ").Append(stats.OutgoingMessagesDropped)
                .Append(" (Drop Rate: ").Append((stats.OutgoingDropRate * 100).ToString("F2")).AppendLine("%)")
                .Append("  Messages Sent: ").AppendLine(stats.MessagesSent.ToString())
                .Append("  Errors Skipped: ").AppendLine(stats.ErrorsSkipped.ToString())
                .Append("  Temp Buffer Allocs: ").AppendLine(stats.TempBufferAllocations.ToString())
                .Append("  Bytes Received: ").AppendLine(stats.BytesReceived.ToString())
                .Append("  Bytes Sent: ").AppendLine(stats.BytesSent.ToString())
                .Append("  Outgoing Channel Pending: ").AppendLine(hasPendingSends ? "Yes" : "No")
                .Append("  Receive Buffer: ").Append(bufferLength).Append('/').Append(bufferCapacity)
                .Append(" bytes (Free: ").Append(bufferFreeSpace).AppendLine(")")
                .Append("  Zero-Byte Receive Mode: ").AppendLine(isZeroByteReceive ? "True" : "False")
                .Append("  Sending: ").AppendLine(isSending ? "True" : "False");

            return _diagnosticInfoBuilder.ToString();
        }

        /// <summary>
        /// Dispose of resources
        /// </summary>
        public void Dispose()
        {
            // Ensure single-threaded disposal.
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 1)
            {
                return;
            }

            Stop();

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finalizer
        /// </summary>
        ~NetClient()
        {
            Dispose();
        }
    }
}
