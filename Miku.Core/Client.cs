using System;
using System.Net;
using System.Buffers;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Miku.Core
{
    public class Client
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
        /// 是否处理粘包
        /// </summary>
        public bool UsePacket = true;
        
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
        
        //封装socket
        internal readonly Socket Socket;

        //接受数据的缓冲区
        private byte[] _buffers;

        //标识是否已经释放
        private volatile bool _isDispose;

        //默认10K的缓冲区空间
        private int _bufferSize = 10 * 1024;

        //每一次接受到的字节数
        private int _receiveSize;

        //接受空消息次数
        private byte _zeroCount;

        //断开回调
        public event Action<string> OnClose;
        
        /// <summary>
        /// 客户端主动请求服务器
        /// </summary>
        /// <param name="ip"></param>
        /// <param name="port"></param>
        /// <param name="maxBufferSize"></param>
        public Client(string ip, int port, int maxBufferSize = 30 * 1024)
        {
            Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Socket.NoDelay = false;
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
        /// 设置socket
        /// </summary>
        private void SetSocket(int size)
        {
            _bufferSize = Math.Max(size, _bufferSize);
            _isDispose = false;
            Socket.ReceiveBufferSize = _bufferSize;
            Socket.SendBufferSize = _bufferSize;
            _buffers = ArrayPool<byte>.Shared.Rent(_bufferSize);
        }
 
        /// <summary>
        /// 接收消息方法
        /// </summary>
        internal async void ReceiveAsync()
        {
            try
            {
                while (!_isDispose && Socket.Connected)
                {
                    //接受消息
                    _receiveSize = await Socket.ReceiveAsync(_buffers, SocketFlags.None, default).ConfigureAwait(false);
                    //判断接受的字节数
                    if (_receiveSize > 0)
                    {
                        try
                        {
                            var ret = new ArraySegment<byte>(_buffers, 0, _receiveSize);
                            OnReceived?.Invoke(ret);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Receive error: {ex.Message}\n{ex.StackTrace}");
                        }
                        //重置连续收到空字节数
                        _zeroCount = 0;
                    }
                    else
                    {
                        _zeroCount++;
                        if (_zeroCount == 5)
                        {
                            Close("connection error");
                        }
                    }
                }
            }
            catch (SocketException)
            {
                Close("connection has been closed");
            }
            catch (ObjectDisposedException)
            {
                Close("connection has been closed");
            }
            catch (Exception ex)
            {
                Close($"{ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// 发送消息方法
        /// </summary>
        public void Send(Span<byte> buffer)
        {
            Send(buffer, UsePacket);
        }

        /// <summary>
        /// 发送消息方法
        /// </summary>
        private unsafe void Send(Span<byte> buffer, bool usePacket)
        {
            try
            {
                if (!_isDispose)
                {
                    if (usePacket)
                    {
                        if (buffer.Length < 1024)
                        {
                            Span<byte> b = stackalloc byte[buffer.Length + 4];
                            Unsafe.As<byte, int>(ref b[0]) = buffer.Length;
                            buffer.CopyTo(b.Slice(4, buffer.Length));
                            Socket.Send(b, SocketFlags.None);
                        }
                        else
                        {
                            var ptr = Marshal.AllocHGlobal(buffer.Length + 4);
                            Span<byte> b = new Span<byte>((byte*)ptr, buffer.Length + 4);
                            Unsafe.As<byte, int>(ref b[0]) = buffer.Length;
                            buffer.CopyTo(b.Slice(4, buffer.Length));
                            Socket.Send(b, SocketFlags.None);
                            Marshal.FreeHGlobal(ptr);
                        }
                    }
                    else
                    {
                        Socket.Send(buffer, SocketFlags.None);
                    }
                }
            }
            catch
            {
                Close("connection has been closed");
            }
        }

        /// <summary>
        /// 发送消息方法
        /// </summary>
        public ValueTask SendAsync(ArraySegment<byte> buffer)
        {
            return SendAsync(buffer, UsePacket);
        }

        /// <summary>
        /// 发送消息方法
        /// </summary>
        private async ValueTask SendAsync(ArraySegment<byte> buffer, bool usePacket)
        {
            try
            {
                if (!_isDispose)
                {
                    if (usePacket)
                    {
                        var b = ArrayPool<byte>.Shared.Rent(buffer.Count + 4);
                        Unsafe.As<byte, int>(ref b[0]) = buffer.Count;
                        buffer.CopyTo(b, 4);
                        await Socket.SendAsync(new ArraySegment<byte>(b, 0, buffer.Count + 4), SocketFlags.None, default).ConfigureAwait(false);
                        ArrayPool<byte>.Shared.Return(b);
                    }
                    else
                    {
                        await Socket.SendAsync(buffer, SocketFlags.None, default).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                Close("connection has been closed");
            }
        }

        /// <summary>
        /// 关闭并释放资源
        /// </summary>
        /// <param name="msg"></param>
        public void Close(string msg = "closed manually")
        {
            if (!_isDispose)
            {
                _isDispose = true;
                try
                {
                    try
                    {
                        Socket.Close();
                    }
                    catch
                    {
                        //ignore
                    }

                    IDisposable disposable = Socket;
                    if (disposable != null)
                    {
                        disposable.Dispose();
                    }

                    GC.SuppressFinalize(this);
                }
                catch (Exception)
                {
                    //ignore
                }
                ArrayPool<byte>.Shared.Return(_buffers);
                OnClose?.Invoke(msg);
            }
        }
    }
}