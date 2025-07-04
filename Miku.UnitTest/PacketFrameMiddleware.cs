using System.Buffers;
using System.Buffers.Binary;
using Miku.Core;

namespace Miku.UnitTest;

/// <summary>
/// Middleware for processing packets. Prevent framing due to tcp.
/// </summary>
public class PacketFrameMiddleware : INetMiddleware
{

    public void ProcessSend(ReadOnlyMemory<byte> input, ArrayBufferWriter<byte> output)
    {
        var memory = output.GetMemory(input.Length + 4);
        // Write the length of the packet to the buffer.
        BinaryPrimitives.WriteUInt32LittleEndian(memory.Span, (uint)input.Length);
        // Copy the packet to the buffer.
        input.Span.CopyTo(memory.Span.Slice(4));
        // Set the output to the buffer.
        output.Advance(input.Length + 4);
        Console.WriteLine($"Send: {string.Join(',', output.WrittenMemory.Span.ToArray())}");
    }

    public (bool halt, int consumedFromOrigin) ProcessReceive(ReadOnlyMemory<byte> input,
        ArrayBufferWriter<byte> output)
    {
        // If we don't have enough data to read the length of the packet, we need to wait for more data.
        if (input.Length < 4)
        {
            return (true, 0);
        }

        // Read the length of the packet.
        var length = BinaryPrimitives.ReadUInt32LittleEndian(input.Span);
        // Ensure the length of the packet is valid.
        if (length > input.Length - 4)
        {
            return (true, 0);
        }

        Console.WriteLine($"Receive: {string.Join(',', input.ToArray())}");
        // Advance the input to the start of the packet.
        output.Write(input.Slice(4, (int)length).Span);
        // Set the consumed from origin to 4 + the length of the packet.
        return (false, 4 + (int)length);
    }
}