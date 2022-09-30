using System;
using System.Runtime.CompilerServices;

namespace Miku.Core
{
    public readonly struct Packet //as I no longer needs to store span, no need for ref struct -> we can parse packets in async functions now, yay
    {
        /// <summary>
        /// Received binary data from remote
        /// </summary>
        private readonly ArraySegment<byte> _buffer;

        /// <summary>
        /// Length of data, excluded the header
        /// </summary>
        public int Length =>
            Unsafe.As<byte, int>(ref _buffer.AsSpan()[0]); //don't return ref value as we want a copy of the length

        /// <summary>
        /// Data in the packet
        /// </summary>
        public ArraySegment<byte> Data //I was going to store span here but realised you can not use it in async methods, but you can use arraysegment lol
        {
            get
            {
                //TODO XOR injection
                return _buffer.Slice(4, Length);
            }
        }

        /// <summary>
        /// Whether this packet is valid
        /// </summary>
        public bool Valid => _buffer.Array != null && _buffer.Count >= 4 && _buffer.Count >= 4 + Length;

        /// <summary>
        /// Get the next packet
        /// </summary>
        public Packet NextPacket
        {
            get
            {
                var totalLen = 4 + Length;
                if (_buffer.Count < totalLen + 4)
                {
                    return new Packet(Array.Empty<byte>());
                }

                return new Packet(_buffer.Slice(totalLen));
            }
        }

        /// <summary>
        /// Parse a received message to packet
        /// </summary>
        /// <param name="buffer"></param>
        public Packet(ArraySegment<byte> buffer)
        {
            _buffer = buffer;
        }
    }
}