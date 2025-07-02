using System;

public readonly struct MultiMemory<T>
{
    public readonly Memory<T> First;
    public readonly Memory<T> Second;

    public MultiMemory(Memory<T> first, Memory<T> second)
    {
        First = first;
        Second = second;
    }

    public static MultiMemory<T> Empty => new(Memory<T>.Empty, Memory<T>.Empty);
    public bool IsEmpty => First.IsEmpty && Second.IsEmpty;
    public int Length => First.Length + Second.Length;

    public void CopyTo(Span<T> span)
    {
        if (span.Length < Length)
            throw new ArgumentException("Span is too small to copy the data.");

        First.Span.CopyTo(span);
        Second.Span.CopyTo(span.Slice(First.Length));
    }
}