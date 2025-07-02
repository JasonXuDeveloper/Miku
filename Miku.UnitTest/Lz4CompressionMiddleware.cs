using System.Buffers;
using K4os.Compression.LZ4;
using Miku.Core;

namespace Miku.UnitTest;

/// <summary>
/// Middleware that compresses and decompresses data using the LZ4 algorithm.
/// </summary>
public class Lz4CompressionMiddleware : INetMiddleware
{

    public void ProcessSend(ReadOnlyMemory<byte> input, ArrayBufferWriter<byte> output)
    {
        LZ4Pickler.Pickle(input.Span, output);
    }

    public (bool halt, int consumedFromOrigin) ProcessReceive(ReadOnlyMemory<byte> input,
        ArrayBufferWriter<byte> output)
    {
        try
        {
            LZ4Pickler.Unpickle(input.Span, output);
            return (false, 0);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return (true, 0);
        }
    }
}