using System;
using System.Net;
using System.Linq;
using System.Threading;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Miku.Core
{
    public class Server
    {
        #region CUSTOM

        /// <summary>
        /// 是否处理粘包
        /// </summary>
        public bool UsePacket = true;

        /// <summary>
        /// 最大客户端和服务端直接缓冲区的大小（默认30KB）
        /// </summary>
        public int MaxBufferSize = 30 * 1024;

        #endregion

        /// <summary>
        /// 获取绑定终结点
        /// </summary>
        public IPEndPoint Ip => _ip;

        /// <summary>
        /// 监听地址
        /// </summary>
        private readonly IPEndPoint _ip;

        /// <summary>
        /// TCP监听Socket
        /// </summary>
        private readonly Socket _listeners;

        /// <summary>
        /// 是否被释放
        /// </summary>
        private volatile bool _disposed;

        /// <summary>
        /// 客户端
        /// </summary>
        private readonly ConcurrentDictionary<ulong, Client> _clients =
            new ConcurrentDictionary<ulong, Client>();

        /// <summary>
        /// 专门用来启动标记的客户端
        /// </summary>
        private readonly ConcurrentQueue<Client> _clientsToStart = new ConcurrentQueue<Client>();

        /// <summary>
        /// 客户端连接回调
        /// </summary>
        public event Action<uint> OnConnect;

        /// <summary>
        /// 客户端发来消息回调
        /// </summary>
        public event Action<uint, ArraySegment<byte>> OnMessage;

        /// <summary>
        /// 客户端断开回调
        /// </summary>
        public event Action<uint, string> OnDisconnect;

        /// <summary>
        /// 是否在运行
        /// </summary>
        public bool IsRunning { get; private set; }

        /// <summary>
        /// 获取客户端
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        private Client GetClient(uint id)
        {
            if (_clients.TryGetValue(id, out var client))
            {
                return client;
            }

            return null;
        }

        /// <summary>
        /// 客户端是否在线
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public bool ClientOnline(uint id)
        {
            var socket = GetClient(id)?.Socket;
            try
            {
                if (socket == null) return false;
                if (!socket.Connected) return false;
                return !(socket.Poll(1, SelectMode.SelectRead) && socket.Available == 0);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 把客户端踢下线
        /// </summary>
        /// <param name="id"></param>
        public void KickClient(uint id)
        {
            if (!_clients.ContainsKey(id)) return;
            _clients[id].Close("server kicked this client");
        }

        /// <summary>
        /// 给客户端发消息
        /// </summary>
        /// <param name="id"></param>
        /// <param name="message"></param>
        public void SendToClient(uint id, Span<byte> message)
        {
            if (!_clients.TryGetValue(id, out var client)) return;
            client.Send(message);
        }

        /// <summary>
        /// 给客户端发消息
        /// </summary>
        /// <param name="id"></param>
        /// <param name="message"></param>
        public async ValueTask SendToClientAsync(uint id, ArraySegment<byte> message)
        {
            if (!_clients.TryGetValue(id, out var client)) return;
            await client.SendAsync(message);
        }

        /// <summary>
        /// 初始化服务器
        /// </summary>
        public Server(int port)
        {
            _disposed = false;
            IPEndPoint localEp = new IPEndPoint(IPAddress.Parse("0.0.0.0"), port);
            _ip = localEp;
            try
            {
                _listeners = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                Dispose();
            }
        }

        /// <summary>
        /// 启动
        /// </summary>
        public void Start()
        {
            _listeners.Bind(_ip);
            _listeners.Listen(3000);
            Console.WriteLine($"Listen Tcp -> {Ip} ");
            IsRunning = true;
            //单独一个线程检测连接
            new Thread(AcceptAsync).Start();
            //单独一个线程检测客户端是否在线
            new Thread(CheckStatus).Start();
            //单独一个线程启动客户端
            new Thread(StartClients).Start();
            //单独一个线程处理GC
            new Thread(() =>
            {
                while (IsRunning)
                {
                    for (int i = 0; i < 10; i++)
                    {
                        GC.Collect(0, GCCollectionMode.Forced);
                        GC.Collect(1, GCCollectionMode.Forced);
                        GC.Collect(2, GCCollectionMode.Forced);
                    }

                    Thread.Sleep(10000);
                }
            }).Start();
        }

        /// <summary>
        /// 循环监听状态  
        /// </summary>
        private void CheckStatus()
        {
            while (IsRunning)
            {
                //每10s 检查一次
                Thread.Sleep(1000 * 10);
                var ids = _clients.Keys.ToArray();
                foreach (uint id in ids)
                {
                    if (!ClientOnline(id))
                    {
                        GetClient(id)?.Close();
                    }
                }
            }
        }

        /// <summary>
        /// 循环启动客户端
        /// </summary>
        private void StartClients()
        {
            while (IsRunning)
            {
                int cnt = _clientsToStart.Count;
                while (cnt-- > 0)
                {
                    if (_clientsToStart.TryDequeue(out var client))
                    {
                        client.Start();
                    }
                }

                Thread.Sleep(1);
            }
        }

        /// <summary>
        /// 当前连接的客户端id
        /// </summary>
        private uint _curId;

        /// <summary>
        /// 异步接收
        /// </summary>
        private async void AcceptAsync()
        {
            while (IsRunning)
            {
                try
                {
                    var socket = await _listeners.AcceptAsync().ConfigureAwait(false);
                    socket.NoDelay = false;
                    Interlocked.Increment(ref _curId);
                    var id = _curId;
                    var client = new Client(socket, MaxBufferSize);
                    client.Socket.ReceiveTimeout = 1000 * 60 * 5; //5分钟没收到东西就算超时
                    client.UsePacket = UsePacket;//是否处理粘包
                    if (_clients.TryAdd(id, client))
                    {
                        client.OnReceived += arr => { OnMessage?.Invoke(id, arr); };
                        client.OnClose += msg =>
                        {
                            _clients.TryRemove(id, out _);
                            OnDisconnect?.Invoke(id, msg);
                        };
                        OnConnect?.Invoke(id);
                        _clientsToStart.Enqueue(client);
                    }
                    else
                    {
                        Console.WriteLine(
                            $"ERROR WITH Remote Socket LocalEndPoint：{socket.LocalEndPoint} RemoteEndPoint：{socket.RemoteEndPoint}");
                        Console.WriteLine();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                IsRunning = false;
                Dispose(true);
                GC.SuppressFinalize(this);
            }
        }

        /// <summary>
        /// 释放所占用的资源
        /// </summary>
        /// <param name="flag1"></param>
        protected virtual void Dispose([MarshalAs(UnmanagedType.U1)] bool flag1)
        {
            if (flag1)
            {
                if (_listeners != null)
                {
                    try
                    {
                        Console.WriteLine("Stop Listener Tcp -> {0}:{1} ", Ip.Address, Ip.Port);
                        _listeners.Close();
                        _listeners.Dispose();
                    }
                    catch
                    {
                        //ignore
                    }
                }
            }
        }
    }
}