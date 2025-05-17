using System.Buffers;
using K4os.Compression.LZ4;
using Miku.Core;

namespace Miku.UnitTest;

/// <summary>
/// Middleware that compresses and decompresses data using the LZ4 algorithm.
/// </summary>
public class Lz4CompressionMiddleware : INetMiddleware
{
    private readonly ArrayBufferWriter<byte> _buffer = new ArrayBufferWriter<byte>();

    public void ProcessSend(ref ReadOnlyMemory<byte> input, out ReadOnlyMemory<byte> output)
    {
        _buffer.Clear();
        LZ4Pickler.Pickle(input.Span, _buffer);
        output = _buffer.WrittenMemory;
    }

    public (bool halt, int consumedFromOrigin) ProcessReceive(ref ReadOnlyMemory<byte> input,
        out ReadOnlyMemory<byte> output)
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