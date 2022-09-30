using System;
using System.Buffers;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Miku.Core
{
    public abstract class ClientBase
    {
        //封装socket
        internal Socket Socket;

        //回调
        private AsyncCallback _aCallback;

        //接受数据的缓冲区
        private byte[] _buffers;

        //标识是否已经释放
        private volatile bool _isDispose;

        //默认10K的缓冲区空间
        private int _bufferSize = 10 * 1024;

        //收取消息状态码
        private SocketError _receiveError;

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
            _aCallback = ReceiveCallback;
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

                    ArrayPool<byte>.Shared.Return(_buffers);
                    GC.SuppressFinalize(this);
                }
                catch (Exception)
                {
                    //ignore
                }
                OnClose?.Invoke(msg);
            }
        }


        /// <summary>
        /// 递归接收消息方法
        /// </summary>
        internal void ReceiveAsync()
        {
            try
            {
                if (!_isDispose && Socket.Connected)
                {
                    Socket.BeginReceive(_buffers, 0, _bufferSize, SocketFlags.None, out _receiveError,
                        _aCallback, this);
                    CheckSocketError(_receiveError);
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
                Close(ex.Message);
            }
        }

        /// <summary>
        /// 接收消息回调函数
        /// </summary>
        /// <param name="iar"></param>
        private void ReceiveCallback(IAsyncResult iar)
        {
            if (!_isDispose)
            {
                try
                {
                    //接受消息
                    _receiveSize = Socket.EndReceive(iar, out _receiveError);
                    //检查状态码
                    if (!CheckSocketError(_receiveError) && SocketError.Success == _receiveError)
                    {
                        //判断接受的字节数
                        if (_receiveSize > 0)
                        {
                            Receive(new ArraySegment<byte>(_buffers, 0, _receiveSize));
                            //重置连续收到空字节数
                            _zeroCount = 0;
                            //继续开始异步接受消息
                            ReceiveAsync();
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
                    Close(ex.Message);
                }
            }
        }

        /// <summary>
        /// 错误判断
        /// </summary>
        /// <param name="socketError"></param>
        /// <returns></returns>
        private bool CheckSocketError(SocketError socketError)
        {
            switch ((socketError))
            {
                case SocketError.SocketError:
                case SocketError.VersionNotSupported:
                case SocketError.TryAgain:
                case SocketError.ProtocolFamilyNotSupported:
                case SocketError.ConnectionAborted:
                case SocketError.ConnectionRefused:
                case SocketError.ConnectionReset:
                case SocketError.Disconnecting:
                case SocketError.HostDown:
                case SocketError.HostNotFound:
                case SocketError.HostUnreachable:
                case SocketError.NetworkDown:
                case SocketError.NetworkReset:
                case SocketError.NetworkUnreachable:
                case SocketError.NoData:
                case SocketError.OperationAborted:
                case SocketError.Shutdown:
                case SocketError.SystemNotReady:
                case SocketError.TooManyOpenSockets:
                    Close(socketError.ToString());
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 发送消息方法
        /// </summary>
        public async ValueTask<int> Send(ArraySegment<byte> buffer)
        {
            int size = 0;
            try
            {
                if (!_isDispose)
                {
                    size  = await Socket.SendAsync(buffer, SocketFlags.None);
                }
            }
            catch
            {
                Close("connection has been closed");
            }

            return size;
        }
    }
}