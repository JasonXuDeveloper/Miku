using System;
using System.Buffers;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;

namespace Miku.Core
{
    public abstract class ClientBase
    {
        //封装socket
        internal Socket Socket;

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
        /// 接收回调
        /// </summary>
        /// <param name="buf"></param>
        protected abstract void Receive(ArraySegment<byte> buf);

        /// <summary>
        /// 设置socket
        /// </summary>
        protected void SetSocket(int size)
        {
            _bufferSize = Math.Max(size, _bufferSize);
            _isDispose = false;
            Socket.ReceiveBufferSize = _bufferSize;
            Socket.SendBufferSize = _bufferSize;
            _buffers = ArrayPool<byte>.Shared.Rent(_bufferSize);
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
                            Receive(new ArraySegment<byte>(_buffers, 0, _receiveSize));
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
        public unsafe void Send(Span<byte> buffer, bool usePacket = true)
        {
            try
            {
                if (!_isDispose)
                {
                    if (usePacket)
                    {
                        Span<byte> b = stackalloc byte[buffer.Length + 4];
                        Unsafe.As<byte, int>(ref b[0]) = buffer.Length;
                        buffer.CopyTo(b.Slice(4, buffer.Length));
                        Socket.Send(b, SocketFlags.None);
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
        internal void Send(StreamBuffer buffer)
        {
            try
            {
                if (!_isDispose)
                {
                    //quick copy to ensure data is safe
                    var buf = buffer.GetBuffer();
                    Socket.Send(buf, SocketFlags.None);
                    buffer.Reset();
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
        public async ValueTask SendAsync(ArraySegment<byte> buffer, bool usePacket = true)
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
                        await Socket.SendAsync(new ArraySegment<byte>(b, 0, buffer.Count + 4), SocketFlags.None,
                            default).ConfigureAwait(false);
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
        /// 发送消息方法
        /// </summary>
        internal async ValueTask SendAsync(StreamBuffer buffer)
        {
            try
            {
                if (!_isDispose)
                {
                    //quick copy to ensure data is safe
                    var buf = buffer.GetBuffer();
                    await Socket.SendAsync(buf, SocketFlags.None, default).ConfigureAwait(false);
                    buffer.Reset();
                }
            }
            catch
            {
                Close("connection has been closed");
            }
        }
    }
}