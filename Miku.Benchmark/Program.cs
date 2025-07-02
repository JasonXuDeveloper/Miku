using BenchmarkDotNet.Running;
using Miku.Benchmark;

public class Program
{
    public static void Main(string[] args)
    {
        BenchmarkRunner.Run<MessageBenchmark>();
    }
}
