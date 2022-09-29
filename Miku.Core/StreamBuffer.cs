using System;
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

        public bool Full(Span<byte> data)
        {
            lock (_arr)
            {
                var arrLen = _arr.Length;
                var dLen = data.Length;
                return arrLen - dLen < _pos;
            }
        }
        
        public void Write(Span<byte> data)
        {
            lock (_arr)
            {
                var arrLen = _arr.Length;
                var dLen = data.Length;
                if(arrLen - dLen < _pos)
                {
                    var newArr = System.Buffers.ArrayPool<byte>.Shared.Rent(arrLen * 2);
                    Buffer.BlockCopy(_arr, 0, newArr, 0, arrLen);
                    System.Buffers.ArrayPool<byte>.Shared.Return(_arr);
                    _arr = newArr;
                }
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