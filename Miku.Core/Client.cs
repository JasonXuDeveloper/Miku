using System;
using System.Net;
using System.Net.Sockets;

namespace Miku.Core
{
    public class Client : ClientBase
    {
        /// <summary>
        /// 连上的回调
        /// </summary>
        public event Action OnConnected;
        
        /// <summary>
        /// 收到服务端消息的回调
        /// </summary>
        public event Action<ArraySegment<byte>> OnReceived;

        /// <summary>
        /// 连接的IP
        /// </summary>
        public readonly IPAddress Ip;
        
        /// <summary>
        /// 连接的端口
        /// </summary>
        public readonly int Port;

        /// <summary>
        /// 最大缓冲区长度
        /// </summary>
        public readonly int MaxBufferSize;
        
        /// <summary>
        /// 客户端主动请求服务器
        /// </summary>
        /// <param name="ip"></param>
        /// <param name="port"></param>
        /// <param name="maxBufferSize"></param>
        public Client(string ip, int port, int maxBufferSize = 30 * 1024)
        {
            Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Socket.ReceiveTimeout = 1000 * 60 * 5;//5分钟没收到东西就算超时
            Ip = IPAddress.Parse(ip);
            Port = port;
            MaxBufferSize = maxBufferSize;
        }

        /// <summary>
        /// 连接
        /// </summary>
        public void Connect()
        {
            if (Socket.Connected) return;
            Socket.Connect(Ip, Port);
            Start();
        }
 
        /// <summary>
        /// 这个是服务器收到有效链接初始化
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="maxBufferSize"></param>
        internal Client(Socket socket, int maxBufferSize)
        {
            Socket = socket;
            IPEndPoint remoteIpEndPoint = socket.RemoteEndPoint as IPEndPoint;
            Ip = remoteIpEndPoint?.Address;
            Port = remoteIpEndPoint?.Port ?? 0;
            MaxBufferSize = maxBufferSize;
        }

        /// <summary>
        /// 开始监听
        /// </summary>
        internal void Start()
        {
            SetSocket(MaxBufferSize);
            OnConnected?.Invoke();
            ReceiveAsync();
        }
 
        /// <summary>
        /// 收到消息后
        /// </summary>
        /// <param name="buf"></param>
        protected override void Receive(ArraySegment<byte> buf)
        {
            OnReceived?.Invoke(buf);
        }
    }
}