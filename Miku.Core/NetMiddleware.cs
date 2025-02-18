using System;

namespace Miku.Core
{
    /// <summary>
    /// Middleware for processing data before sending or receiving
    /// </summary>
    public abstract class NetMiddleware
    {
        /// <summary>
        /// Process data before sending
        /// </summary>
        /// <param name="input">A data to be sent (might be processed by other middleware)</param>
        /// <param name="output">A processed data to be sent</param>
        public abstract void ProcessSend(ref Memory<byte> input, out Memory<byte> output);
        
        /// <summary>
        /// Process data after receiving
        /// </summary>
        /// <param name="input">A received data (might be processed by other middleware)</param>
        /// <param name="output">A processed data to be passed to the next middleware</param>
        /// <returns>Whether to halt the processing and how many bytes are consumed</returns>
        public abstract (bool halt, int consumedFromOrigin) ProcessReceive(ref ReadOnlyMemory<byte> input, out ReadOnlyMemory<byte> output);
    }
}