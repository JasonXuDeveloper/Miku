using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Threading;

namespace Miku.Core
{
    /// <summary>
    /// A client for connecting to a remote host
    /// </summary>
    public class NetClient
    {
        /// <summary>
        /// Event when the client is connected
        /// </summary>
        public event Action OnConnected;

        /// <summary>
        /// Event when the client is disconnected
        /// </summary>
        public event Action OnDisconnected;

        /// <summary>
        /// Event when data is received
        /// </summary>
        public event Action<ReadOnlyMemory<byte>> OnDataReceived;

        /// <summary>
        /// Event when an error occurred
        /// </summary>
        public event Action<Exception> OnError;

        /// <summary>
        /// Unique identifier for the client
        /// </summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>
        /// Whether the client is connected
        /// </summary>
        public bool IsConnected => Volatile.Read(ref _isConnected) == 1;

        /// <summary>
        /// Remote host IP address
        /// </summary>
        public string Ip { get; private set; }

        private readonly List<INetMiddleware> _middlewares = new();
        private Socket _socket;
        private int _isConnected;
        private int _sending;

        private SocketAsyncEventArgs _receiveArg;
        private SocketAsyncEventArgs _sendArg;
        private ArrayBufferWriter<byte> _receivedData;
        private ConcurrentQueue<ArraySegment<byte>> _sendQueue;

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
            if (IsConnected)
            {
                throw new InvalidOperationException("Already connected");
            }

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _socket.Connect(ip, port);
            _socket.NoDelay = true;
            Interlocked.Exchange(ref _isConnected, 1);
            Ip = ((System.Net.IPEndPoint)_socket.RemoteEndPoint!).Address.ToString();

            _receivedData = new ArrayBufferWriter<byte>(bufferSize);
            _sendQueue = new ConcurrentQueue<ArraySegment<byte>>();

            _receiveArg = new SocketAsyncEventArgs();
            _receiveArg.SetBuffer(ArrayPool<byte>.Shared.Rent(bufferSize), 0, bufferSize);
            _receiveArg.UserToken = this;
            _receiveArg.Completed += HandleReadWrite;

            _sendArg = new SocketAsyncEventArgs();
            _sendArg.UserToken = this;
            _sendArg.Completed += HandleReadWrite;

            OnConnected?.Invoke();

            if (!_socket.ReceiveAsync(_receiveArg))
            {
                Receive(_receiveArg);
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
            if (IsConnected)
            {
                throw new InvalidOperationException("Already connected");
            }

            _socket = socket;
            _socket.NoDelay = true;
            Interlocked.Exchange(ref _isConnected, 1);
            Ip = ((System.Net.IPEndPoint)socket.RemoteEndPoint!).Address.ToString();

            _receivedData = new ArrayBufferWriter<byte>(bufferSize);
            _sendQueue = new ConcurrentQueue<ArraySegment<byte>>();

            _receiveArg = new SocketAsyncEventArgs();
            _receiveArg.SetBuffer(ArrayPool<byte>.Shared.Rent(bufferSize), 0, bufferSize);
            _receiveArg.UserToken = this;
            _receiveArg.Completed += HandleReadWrite;

            _sendArg = new SocketAsyncEventArgs();
            _sendArg.UserToken = this;
            _sendArg.Completed += HandleReadWrite;

            OnConnected?.Invoke();

            if (!_socket.ReceiveAsync(_receiveArg))
            {
                Receive(_receiveArg);
            }
        }

        /// <summary>
        /// Stop the client
        /// </summary>
        public void Stop()
        {
            if (!IsConnected)
            {
                return;
            }

            Interlocked.Exchange(ref _isConnected, 0);
            Interlocked.Exchange(ref _sending, 0);

            var receiveBuffer = _receiveArg.Buffer;
            _socket?.Shutdown(SocketShutdown.Both);
            _socket?.Close();
            _socket?.Dispose();
            _socket = null;

            _receiveArg.Completed -= HandleReadWrite;
            _sendArg.Completed -= HandleReadWrite;
            try
            {
                _receiveArg.Dispose();
                _sendArg.Dispose();
            }
            catch
            {
                // ignore
            }

            // return buffer
            if (receiveBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(receiveBuffer);
            }

            while (_sendQueue.TryDequeue(out var segment))
            {
                ArrayPool<byte>.Shared.Return(segment.Array!);
            }

            try
            {
                OnDisconnected?.Invoke();
            }
            catch (Exception e)
            {
                OnError?.Invoke(e);
            }
        }

        /// <summary>
        /// Send data to the remote host
        /// </summary>
        /// <param name="data"></param>
        /// <returns>Whether the data is sent successfully</returns>
        /// <exception cref="InvalidOperationException"></exception>
        public void Send(ReadOnlyMemory<byte> data)
        {
            if (!IsConnected)
            {
                OnError?.Invoke(new InvalidOperationException("Not connected"));
                return;
            }

            // process through middlewares
            foreach (var middleware in _middlewares)
            {
                try
                {
                    middleware.ProcessSend(ref data, out data);
                }
                catch (Exception e)
                {
                    OnError?.Invoke(e);
                    return;
                }
            }

            var tempSendBuffer = ArrayPool<byte>.Shared.Rent(data.Length);
            data.Span.CopyTo(tempSendBuffer);

            foreach (var middleware in _middlewares)
            {
                try
                {
                    middleware.PostSend();
                }
                catch (Exception e)
                {
                    OnError?.Invoke(e);
                }
            }

            _sendQueue.Enqueue(new ArraySegment<byte>(tempSendBuffer, 0, data.Length));
            ProcessSend();
        }

        private void ProcessSend()
        {
            if (!IsConnected)
            {
                while (_sendQueue.TryDequeue(out var segment))
                {
                    ArrayPool<byte>.Shared.Return(segment.Array!);
                }

                return;
            }

            if (Interlocked.CompareExchange(ref _sending, 1, 0) == 1)
            {
                return;
            }

            var sock = _socket;
            if (sock == null || Volatile.Read(ref _isConnected) == 0)
            {
                Interlocked.Exchange(ref _sending, 0);
                return;
            }

            if (!_sendQueue.TryPeek(out var seg))
            {
                Interlocked.Exchange(ref _sending, 0);
                return;
            }

            _sendArg.SetBuffer(seg);

            try
            {
                if (!sock.SendAsync(_sendArg))
                {
                    HandleReadWrite(null, _sendArg);
                }
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref _sending, 0);
                OnError?.Invoke(e);
                Stop();
            }
        }

        private static void HandleReadWrite(object sender, SocketAsyncEventArgs args)
        {
            switch (args.LastOperation)
            {
                case SocketAsyncOperation.Send:
                    NetClient client = (NetClient)args.UserToken!;
                    if (client._sendQueue.TryDequeue(out var seg))
                    {
                        // return buffer
                        ArrayPool<byte>.Shared.Return(seg.Array!);
                        // set buffer
                        args.SetBuffer(null, 0, 0);
                    }

                    Interlocked.Exchange(ref client._sending, 0);

                    //check connection
                    if (args.SocketError != SocketError.Success)
                    {
                        client.OnError?.Invoke(new SocketException((int)args.SocketError));
                        Stop(args);
                    }
                    // process next send
                    else if (!client._sendQueue.IsEmpty)
                    {
                        client.ProcessSend();
                    }

                    break;
                case SocketAsyncOperation.Receive:
                    //continue receive
                    Receive(args);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown operation: {args.LastOperation}");
            }
        }

        private static void Stop(SocketAsyncEventArgs args)
        {
            NetClient client = (NetClient)args.UserToken!;
            client.Stop();
        }

        private static void Receive(SocketAsyncEventArgs args)
        {
            var client = (NetClient)args.UserToken!;
            var sock = client._socket;
            if (!client.IsConnected || sock == null)
                return;

            // handle socket‐level close/errors
            if (args.SocketError != SocketError.Success || args.BytesTransferred == 0)
            {
                client.Stop();
                return;
            }

            // check if the remote host closed the connection
            if (args is { BytesTransferred: > 0, SocketError: SocketError.Success })
            {
                bool hasLeftover = client._receivedData.WrittenCount > 0;
                ReadOnlyMemory<byte> receivedData = new(args.Buffer, 0, args.BytesTransferred);
                ReadOnlyMemory<byte> remainder;
                if (hasLeftover)
                {
                    // copy
                    client._receivedData.Write(receivedData.Span);
                    Process(client, client._receivedData.WrittenMemory, out remainder);
                }
                else
                {
                    Process(client, receivedData, out remainder);
                }

                if (!remainder.IsEmpty)
                {
                    if (hasLeftover)
                    {
                        byte[] temp = ArrayPool<byte>.Shared.Rent(remainder.Length);
                        remainder.Span.CopyTo(temp);
                        client._receivedData.Clear();
                        client._receivedData.Write(temp);
                        ArrayPool<byte>.Shared.Return(temp);
                    }
                    else
                    {
                        client._receivedData.Write(remainder.Span);
                    }
                }

                if (client._socket != null)
                {
                    if (!client._socket.ReceiveAsync(args))
                        Receive(args);
                }
                else
                {
                    Stop(args);
                }
            }
            else
            {
                Stop(args);
            }
        }

        private static void Process(NetClient client, ReadOnlyMemory<byte> src, out ReadOnlyMemory<byte> remainder)
        {
            remainder = ReadOnlyMemory<byte>.Empty;

            if (src.IsEmpty)
                return;
            int totalConsumed = 0;
            ReadOnlyMemory<byte> processData = src;

            while (!processData.IsEmpty)
            {
                // reverse order - last middleware first
                if (client._middlewares.Count == 0)
                {
                    totalConsumed += processData.Length;
                }
                else
                {
                    for (int i = client._middlewares.Count - 1; i >= 0; i--)
                    {
                        var middleware = client._middlewares[i];
                        try
                        {
                            var (halt, consumed) = middleware.ProcessReceive(ref processData, out processData);
                            // some middlewares might halt the processing
                            if (halt)
                            {
                                return;
                            }

                            totalConsumed += consumed;
                        }
                        catch (Exception e)
                        {
                            client._receivedData.Clear();
                            client.OnError?.Invoke(e);
                            return;
                        }
                    }
                }

                // invoke event
                try
                {
                    client.OnDataReceived?.Invoke(processData);
                }
                catch (Exception e)
                {
                    client.OnError?.Invoke(e);
                }

                for (int i = client._middlewares.Count - 1; i >= 0; i--)
                {
                    var middleware = client._middlewares[i];
                    try
                    {
                        middleware.PostReceive();
                    }
                    catch (Exception e)
                    {
                        client.OnError?.Invoke(e);
                    }
                }

                processData = totalConsumed < src.Length
                    ? src.Slice(totalConsumed)
                    : ReadOnlyMemory<byte>.Empty;
            }

            remainder = totalConsumed < src.Length ? src.Slice(totalConsumed) : ReadOnlyMemory<byte>.Empty;
        }
    }
}