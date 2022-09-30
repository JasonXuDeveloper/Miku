using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Miku.Core
{
    public class StreamBuffer
    {
        private int _pos;
        private byte[] _arr = System.Buffers.ArrayPool<byte>.Shared.Rent(30 * 1024);

        public void Reset()
        {
            Interlocked.Exchange(ref _pos, 0);
        }

        public bool Full(Span<byte> data, bool usePacket)
        {
            lock (_arr)
            {
                var arrLen = _arr.Length;
                var dLen = data.Length + (usePacket ? 4 : 0);
                return arrLen - dLen < _pos;
            }
        }
        
        public void Write(Span<byte> data, bool usePacket)
        {
            lock (_arr)
            {
                var arrLen = _arr.Length;
                var dLen = data.Length;
                //resize
                if(arrLen - dLen - (usePacket ? 4 : 0) < _pos)
                {
                    var newLen = Math.Max(arrLen * 2, dLen + (usePacket ? 4 : 0));
                    var newArr = System.Buffers.ArrayPool<byte>.Shared.Rent(newLen);
                    Buffer.BlockCopy(_arr, 0, newArr, 0, arrLen);
                    System.Buffers.ArrayPool<byte>.Shared.Return(_arr);
                    _arr = newArr;
                }

                //write packet size
                if (usePacket)
                {
                    ref var dest = ref _arr.AsSpan(_pos).GetPinnableReference();
                    Unsafe.WriteUnaligned(ref dest, data.Length);
                    Interlocked.Add(ref _pos, 4);
                }
                
                //write data
                //TODO XOR injection
                data.CopyTo(_arr.AsSpan(_pos, dLen));
                Interlocked.Add(ref _pos, dLen);
            }
        }

        public ArraySegment<byte> GetBuffer()
        {
            if (_pos == 0) return ArraySegment<byte>.Empty;
            lock (_arr)
            {
                var len = _pos;
                Reset();
                return new ArraySegment<byte>(_arr, 0, len);
            }
        }
    }
}