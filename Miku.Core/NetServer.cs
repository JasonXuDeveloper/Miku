using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Miku.Core
{
    /// <summary>
    /// A server that listens for incoming connections from clients.
    /// </summary>
    public class NetServer : IDisposable
    {
        /// <summary>
        /// Event that is raised when a new client connects to the server.
        /// </summary>
        public event Action<NetClient> OnClientConnected;

        /// <summary>
        /// Event that is raised when a client disconnects from the server.
        /// </summary>
        public event Action<NetClient, string> OnClientDisconnected;

        /// <summary>
        /// Event that is raised when a client sends data to the server.
        /// </summary>
        public event Action<NetClient, ReadOnlyMemory<byte>> OnClientDataReceived;

        /// <summary>
        /// Event that is raised when an error occurs.
        /// </summary>
        public event Action<Exception> OnError;

        /// <summary>
        /// The size of the buffer used for sending and receiving data.
        /// </summary>
        public int BufferSize { get; set; } = 64 * 1024; // Increased default buffer size

        private Socket _listenSocket;
        private SocketAsyncEventArgs _acceptEventArgs;
        private readonly List<NetClient> _clients = new();
        private readonly ReaderWriterLockSlim _clientsLock = new();
        private volatile bool _isListening;
        private int _disposed = 0;

        /// <summary>
        /// Whether the server is currently listening for connections
        /// </summary>
        public bool IsListening => _isListening && _disposed == 0;

        /// <summary>
        /// Get current client count in a thread-safe manner
        /// </summary>
        public int ClientCount
        {
            get
            {
                _clientsLock.EnterReadLock();
                try
                {
                    return _clients.Count;
                }
                finally
                {
                    _clientsLock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Starts the server and begins listening for new connections.
        /// </summary>
        /// <param name="ip"></param>
        /// <param name="port"></param>
        /// <param name="backLog"></param>
        public void Start(string ip, int port, int backLog = 1000)
        {
            if (_disposed != 0)
            {
                throw new ObjectDisposedException(nameof(NetServer));
            }

            if (_isListening)
            {
                throw new InvalidOperationException("Server is already listening");
            }

            if (string.IsNullOrWhiteSpace(ip))
            {
                throw new ArgumentException("IP address cannot be null or empty", nameof(ip));
            }

            if (port <= 0 || port > 65535)
            {
                throw new ArgumentException("Port must be between 1 and 65535", nameof(port));
            }

            try
            {
                _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                // Optimize server socket settings
                _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, false);

                _listenSocket.Bind(new IPEndPoint(IPAddress.Parse(ip), port));
                _listenSocket.Listen(backLog);

                // Set up the SocketAsyncEventArgs for accepting connections.
                _acceptEventArgs = new SocketAsyncEventArgs();
                _acceptEventArgs.Completed += ProcessAccept;

                _isListening = true;

                // Start the first accept operation.
                StartAccept();
            }
            catch (Exception ex)
            {
                // Clean up on failure
                _isListening = false;
                _listenSocket?.Close();
                _listenSocket?.Dispose();
                _listenSocket = null;
                _acceptEventArgs?.Dispose();
                _acceptEventArgs = null;
                
                OnError?.Invoke(ex);
                throw;
            }
        }

        /// <summary>
        /// Stops the server from listening for new connections.
        /// </summary>
        public void Stop()
        {
            if (!_isListening) return;

            _isListening = false;

            try
            {
                _listenSocket?.Close();
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
            }
            finally
            {
                _listenSocket?.Dispose();
                _listenSocket = null;
            }

            _clientsLock.EnterWriteLock();
            try
            {
                // Stop all clients, make a copy of the list to avoid modifying it while iterating.
                foreach (var client in _clients.ToArray())
                {
                    try
                    {
                        client.Stop("Server shutting down");
                    }
                    catch (Exception ex)
                    {
                        OnError?.Invoke(ex);
                    }
                }

                _clients.Clear();
            }
            finally
            {
                _clientsLock.ExitWriteLock();
            }

            _acceptEventArgs?.Dispose();
            _acceptEventArgs = null;
        }

        private void StartAccept()
        {
            if (!_isListening) return;

            _acceptEventArgs.AcceptSocket = null;

            if (!_listenSocket.AcceptAsync(_acceptEventArgs))
            {
                ProcessAccept(null, _acceptEventArgs);
            }
        }

        private void ProcessAccept(object sender, SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success)
            {
                // Retrieve the accepted socket.
                Socket acceptedSocket = e.AcceptSocket;

                // Create a new NetClient using the accepted socket.
#pragma warning disable IDISP001
                NetClient client = new NetClient();
#pragma warning restore IDISP001

                _clientsLock.EnterWriteLock();
                try
                {
                    _clients.Add(client);
                }
                finally
                {
                    _clientsLock.ExitWriteLock();
                }

                client.OnConnected += () => OnClientConnected?.Invoke(client);
                client.OnDisconnected += reason =>
                {
                    OnClientDisconnected?.Invoke(client, reason);
                    _clientsLock.EnterWriteLock();
                    try
                    {
                        _clients.Remove(client);
                    }
                    finally
                    {
                        _clientsLock.ExitWriteLock();
                    }
                };
                client.OnDataReceived += data => OnClientDataReceived?.Invoke(client, data);
                client.OnError += OnError;
                client.Connect(acceptedSocket, BufferSize);
            }
            // If the accept operation was canceled, then the server is stopped.
            else if (e.SocketError != SocketError.Interrupted && e.SocketError != SocketError.OperationAborted)
            {
                OnError?.Invoke(new SocketException((int)e.SocketError));
            }

            // Continue accepting the next connection only if still listening
            if (_isListening && _listenSocket != null)
            {
                StartAccept();
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
            {
                Stop();
                _clientsLock?.Dispose();
            }
        }
    }
}