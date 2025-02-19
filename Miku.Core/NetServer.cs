using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Miku.Core
{
    /// <summary>
    /// A server that listens for incoming connections from clients.
    /// </summary>
    public class NetServer
    {
        /// <summary>
        /// Event that is raised when a new client connects to the server.
        /// </summary>
        public event Action<NetClient> OnClientConnected;

        /// <summary>
        /// Event that is raised when a client disconnects from the server.
        /// </summary>
        public event Action<NetClient> OnClientDisconnected;

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
        public int BufferSize { get; set; } = 1024;

        private Socket _listenSocket;
        private SocketAsyncEventArgs _acceptEventArgs;
        private readonly List<NetClient> _clients = new List<NetClient>();

        /// <summary>
        /// Starts the server and begins listening for new connections.
        /// </summary>
        /// <param name="ip"></param>
        /// <param name="port"></param>
        /// <param name="backLog"></param>
        public void Start(string ip, int port, int backLog = 100)
        {
            _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listenSocket.Bind(new IPEndPoint(IPAddress.Parse(ip), port));
            _listenSocket.Listen(backLog);

            // Set up the SocketAsyncEventArgs for accepting connections.
            _acceptEventArgs = new SocketAsyncEventArgs();
            _acceptEventArgs.Completed += ProcessAccept;

            // Start the first accept operation.
            StartAccept();
        }

        /// <summary>
        /// Stops the server from listening for new connections.
        /// </summary>
        public void Stop()
        {
            try
            {
                _listenSocket?.Close();
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
            }

            lock (_clients)
            {
                // Stop all clients, make a copy of the list to avoid modifying it while iterating.
                foreach (var client in _clients.ToArray())
                {
                    client.Stop();
                }

                _clients.Clear();
            }

            _listenSocket = null;
            _acceptEventArgs = null;
        }

        private void StartAccept()
        {
            // If the accept socket is null, then the server is stopped.
            if (_acceptEventArgs == null)
            {
                return;
            }

            // Clear any previous accepted socket.
            _acceptEventArgs.AcceptSocket = null;

            // Start an asynchronous accept operation.
            try
            {
                if (!_listenSocket.AcceptAsync(_acceptEventArgs))
                {
                    // If AcceptAsync returns false, then accept completed synchronously.
                    ProcessAccept(null, _acceptEventArgs);
                }
            }
            catch (ObjectDisposedException)
            {
                // Ignore ObjectDisposedException. This exception occurs when the socket is closed.
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
            }
        }

        private void ProcessAccept(object sender, SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success)
            {
                // Retrieve the accepted socket.
                Socket acceptedSocket = e.AcceptSocket;

                // Create a new NetClient using the accepted socket.
                NetClient client = new NetClient(BufferSize);
                lock (_clients)
                {
                    _clients.Add(client);
                }

                client.OnConnected += () => OnClientConnected?.Invoke(client);
                client.OnDisconnected += () =>
                {
                    OnClientDisconnected?.Invoke(client);
                    lock (_clients)
                    {
                        _clients.Remove(client);
                    }
                };
                client.OnDataReceived += data => OnClientDataReceived?.Invoke(client, data);
                client.OnError += OnError;
                client.Connect(acceptedSocket);
            }
            // If the accept operation was canceled, then the server is stopped.
            else if (e.SocketError != SocketError.Interrupted && e.SocketError != SocketError.OperationAborted)
            {
                OnError?.Invoke(new SocketException((int)e.SocketError));
            }

            // Continue accepting the next connection.
            StartAccept();
        }
    }
}