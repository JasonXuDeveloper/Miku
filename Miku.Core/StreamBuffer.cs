using System;
using System.Threading;
using System.Runtime.CompilerServices;

namespace Miku.Core
{
    public class StreamBuffer
    {
        private int _pos;
        private byte[] _arr = System.Buffers.ArrayPool<byte>.Shared.Rent(30 * 1024);
        private int _locked;

        public int Length => _arr.Length;
        public bool Valid => _pos > 0;

        /// <summary>
        /// Lock this buffer, if it is already locked then wait until it is unlocked, be aware of deadlock
        /// </summary>
        private void EnterLock()
        {
            //waiter
            SpinWait spinner = default;
            while (true)
            {
                //keep waiting
                spinner.SpinOnce();
                //only returns when lock it from unlock
                if (_locked == 0)
                {
                    //when changed from unlock to lock, means this operation has taken the lock
                    if (Interlocked.CompareExchange(ref _locked, 1, 0) == 0) //0 -> not locked, 1 -> locked
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Exit lock state
        /// </summary>
        private void ExitLock()
        {
            Interlocked.CompareExchange(ref _locked, 0, 1);//if locked (1) -> then unlock (0)
        }

        /// <summary>
        /// Reset the buffer, use this with EnterLock and ExitLock to ensure safety
        /// </summary>
        public void Reset()
        {
            EnterLock();
            _pos = 0;
            ExitLock();
        }
        
        /// <summary>
        /// Write data to buffer, use this with EnterLock and ExitLock to ensure safety
        /// </summary>
        /// <param name="data"></param>
        /// <param name="usePacket"></param>
        /// <returns></returns>
        public bool Write(Span<byte> data, bool usePacket)
        {
            EnterLock();
            var arrLen = _arr.Length;
            var dLen = data.Length;
            //resize
            if(arrLen - dLen - (usePacket ? 4 : 0) < _pos)
            {
                var newLen = Math.Max(arrLen * 2, dLen + (usePacket ? 4 : 0));
                newLen = Math.Max(newLen, 1024 * 128);
                if (newLen <= 1024 * 128)//1个发送流每毫秒能1128KB已经很恐怖了
                {
                    var newArr = System.Buffers.ArrayPool<byte>.Shared.Rent(newLen);
                    Buffer.BlockCopy(_arr, 0, newArr, 0, arrLen);
                    System.Buffers.ArrayPool<byte>.Shared.Return(_arr);
                    _arr = newArr;
                }
                ExitLock();
                return false;
            }

            //write packet size
            if (usePacket)
            {
                Unsafe.As<byte, int>(ref _arr[_pos]) = data.Length;
                Interlocked.Add(ref _pos, 4);
            }
                
            //write data
            //TODO XOR injection
            data.CopyTo(_arr.AsSpan(_pos, dLen));
            Interlocked.Add(ref _pos, dLen);
            ExitLock();
            return true;
        }

        /// <summary>
        /// Get the buffer data, use it with EnterLock and ExitLock to ensure safety
        /// </summary>
        /// <returns></returns>
        public ArraySegment<byte> GetBuffer()
        {
            if (_pos == 0) return ArraySegment<byte>.Empty;
            var len = _pos;
            Reset();
            return new ArraySegment<byte>(_arr, 0, len);
        }
    }
}