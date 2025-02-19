using K4os.Compression.LZ4;
using Miku.Core;

namespace Miku.UnitTest;

/// <summary>
/// Middleware that compresses and decompresses data using the LZ4 algorithm.
/// </summary>
public class Lz4CompressionMiddleware: NetMiddleware
{
    public override void ProcessSend(ref Memory<byte> input, out Memory<byte> output)
    {
        output = LZ4Pickler.Pickle(input.Span);
    }

    public override (bool halt, int consumedFromOrigin) ProcessReceive(ref ReadOnlyMemory<byte> input, out ReadOnlyMemory<byte> output)
    {
        try
        {
            output = LZ4Pickler.Unpickle(input.Span);
            return (false, 0);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            output = ReadOnlyMemory<byte>.Empty;
            return (true, 0);
        }
    }
}