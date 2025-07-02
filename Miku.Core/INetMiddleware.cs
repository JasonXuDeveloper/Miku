using System;
using System.Buffers;

namespace Miku.Core
{
    /// <summary>
    /// Middleware for processing data before sending or receiving
    /// </summary>
    public interface INetMiddleware
    {
        /// <summary>
        /// Process data before sending
        /// </summary>
        /// <param name="src">Source data to be processed</param>
        /// <param name="dst">Destination buffer to write processed data to</param>
        void ProcessSend(ReadOnlyMemory<byte> src, ArrayBufferWriter<byte> dst);

        /// <summary>
        /// Process data after receiving
        /// </summary>
        /// <param name="src">Source data to be processed</param>
        /// <param name="dst">Destination buffer to write processed data to</param>
        /// <returns>Whether to halt the processing and how many bytes are consumed from the original input</returns>
        (bool halt, int consumedFromOrigin) ProcessReceive(ReadOnlyMemory<byte> src, ArrayBufferWriter<byte> dst);
    }
}