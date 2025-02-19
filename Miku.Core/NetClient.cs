using System;
using System.Buffers;
using System.Threading;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
        public bool IsConnected => _isConnected;

        /// <summary>
        /// Remote host IP address
        /// </summary>
        public string Ip { get; private set; }

        private readonly List<NetMiddleware> _middlewares = new();
        private Socket _socket;
        private bool _isConnected;
        private int _sending;

        private readonly SocketAsyncEventArgs _receiveArg;
        private readonly SocketAsyncEventArgs _sendArg;
        private readonly ArrayBufferWriter<byte> _receivedData;
        private byte[] _tempSendBuffer;

        /// <summary>
        /// Create a new instance of NetClient
        /// </summary>
        /// <param name="bufferSize"></param>
        public NetClient(int bufferSize = 1024)
        {
            _receivedData = new ArrayBufferWriter<byte>(bufferSize);

            _receiveArg = new SocketAsyncEventArgs();
            _receiveArg.SetBuffer(new byte[bufferSize], 0, bufferSize);
            _receiveArg.UserToken = this;
            _receiveArg.Completed += HandleReadWrite;

            _sendArg = new SocketAsyncEventArgs();
            _sendArg.UserToken = this;
            _sendArg.Completed += HandleReadWrite;
        }

        /// <summary>
        /// Add a middleware to the client
        /// </summary>
        /// <param name="middleware"></param>
        public void AddMiddleware(NetMiddleware middleware)
        {
            _middlewares.Add(middleware);
        }

        /// <summary>
        /// Remove a middleware from the client
        /// </summary>
        /// <param name="middleware"></param>
        public void RemoveMiddleware(NetMiddleware middleware)
        {
            _middlewares.Remove(middleware);
        }

        /// <summary>
        /// Connect to the remote host
        /// </summary>
        /// <param name="ip"></param>
        /// <param name="port"></param>
        /// <exception cref="InvalidOperationException"></exception>
        public void Connect(string ip, int port)
        {
            if (_isConnected)
            {
                throw new InvalidOperationException("Already connected");
            }

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _socket.Connect(ip, port);
            _isConnected = true;
            Ip = ((System.Net.IPEndPoint)_socket.RemoteEndPoint!).Address.ToString();

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
        /// <exception cref="InvalidOperationException"></exception>
        internal void Connect(Socket socket)
        {
            if (_isConnected)
            {
                throw new InvalidOperationException("Already connected");
            }

            _socket = socket;
            _isConnected = true;
            Ip = ((System.Net.IPEndPoint)socket.RemoteEndPoint!).Address.ToString();

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
            if (!_isConnected)
            {
                return;
            }

            _isConnected = false;
            _socket?.Shutdown(SocketShutdown.Both);
            _socket?.Close();
            _socket?.Dispose();
            _socket = null;

            if (_tempSendBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(_tempSendBuffer);
                _tempSendBuffer = null;
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
        public bool Send(Memory<byte> data)
        {
            if (!_isConnected)
            {
                throw new InvalidOperationException("Not connected");
            }

            // ensure only one sending operation at a time, no concurrent sending, by using Interlocked
            if (Interlocked.CompareExchange(ref _sending, 1, 0) == 1)
            {
                return false;
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
                    // reset sending flag
                    Interlocked.Exchange(ref _sending, 0);
                    return false;
                }
            }

            _tempSendBuffer = ArrayPool<byte>.Shared.Rent(data.Length);
            data.Span.CopyTo(_tempSendBuffer);
            _sendArg.SetBuffer(_tempSendBuffer, 0, data.Length);

            if (!_socket.SendAsync(_sendArg))
            {
                HandleReadWrite(null, _sendArg);
            }

            return true;
        }

        private static void HandleReadWrite(object sender, SocketAsyncEventArgs args)
        {
            switch (args.LastOperation)
            {
                case SocketAsyncOperation.Send:
                    NetClient client = (NetClient)args.UserToken!;
                    // return buffer
                    ArrayPool<byte>.Shared.Return(client._tempSendBuffer!);
                    client._tempSendBuffer = null;
                    args.SetBuffer(null, 0, 0);
                    // reset sending flag
                    Interlocked.Exchange(ref client._sending, 0);

                    //check connection
                    if (args.SocketError != SocketError.Success)
                    {
                        client.OnError?.Invoke(new SocketException((int)args.SocketError));
                        Stop(args);
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
            NetClient client = (NetClient)args.UserToken!;

            // check if the remote host closed the connection
            if (args is { BytesTransferred: > 0, SocketError: SocketError.Success })
            {
                try
                {
                    ReadOnlySpan<byte> receivedData = new(args.Buffer, 0, args.BytesTransferred);
                    // copy
                    client._receivedData.Write(receivedData);

                    // process through middlewares, reverse order
                    ReadOnlyMemory<byte> processedData = client._receivedData.WrittenMemory;
                    // temp stack memory
                    Span<byte> tempStackMem = stackalloc byte[1024];
                    // repeat until all data is processed
                    while (!processedData.IsEmpty)
                    {
                        int index = 0;
                        // reverse order - last middleware first
                        for (int i = client._middlewares.Count - 1; i >= 0; i--)
                        {
                            var middleware = client._middlewares[i];
                            try
                            {
                                var (halt, consumed) = middleware.ProcessReceive(ref processedData, out processedData);
                                // some middlewares might halt the processing
                                if (halt)
                                {
                                    goto cont_receive;
                                }

                                index += consumed;
                            }
                            catch (Exception e)
                            {
                                client.OnError?.Invoke(e);
                                goto cont_receive;
                            }
                        }

                        // still the original data or partially the original data
                        var offset = Unsafe.ByteOffset(ref MemoryMarshal.GetReference(processedData.Span),
                            ref MemoryMarshal.GetReference(client._receivedData.WrittenSpan));
                        // if offset is less than length of _receivedData.WrittenMemory, then it's the original data
                        if ((long)offset < client._receivedData.WrittenCount)
                        {
                            index = (int)offset + processedData.Length;
                        }

                        // invoke event
                        try
                        {
                            client.OnDataReceived?.Invoke(processedData);
                        }
                        catch (Exception e)
                        {
                            client.OnError?.Invoke(e);
                        }

                        // get left over data
                        int leftOver = client._receivedData.WrittenCount - index;
                        if (leftOver > 0 && index < client._receivedData.WrittenCount && index > 0)
                        {
                            // copy left over data to temp stack memory - faster than array pool
                            if (leftOver <=1024)
                            {
                                client._receivedData.WrittenSpan.Slice(index).CopyTo(tempStackMem);
                                client._receivedData.Clear();
                                client._receivedData.Write(tempStackMem.Slice(0, leftOver));
                            }
                            else
                            {
                                byte[] temp = ArrayPool<byte>.Shared.Rent(leftOver);
                                client._receivedData.WrittenMemory.Slice(index).CopyTo(temp);
                                client._receivedData.Clear();
                                client._receivedData.Write(temp.AsSpan(0, leftOver));
                                ArrayPool<byte>.Shared.Return(temp);
                            }
                        }
                        else
                        {
                            client._receivedData.Clear();
                        }

                        processedData = client._receivedData.WrittenMemory;
                    }
                }
                catch (Exception e)
                {
                    client.OnError?.Invoke(e);
                    goto cont_receive;
                }

                if (!client._isConnected || client._socket == null)
                {
                    return;
                }

                cont_receive:
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
    }
}