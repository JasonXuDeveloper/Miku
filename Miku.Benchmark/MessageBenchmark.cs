#pragma warning disable CS8618
using BenchmarkDotNet.Attributes;
using Miku.Core;
using System;
using System.Threading.Tasks;

namespace Miku.Benchmark
{
    [MemoryDiagnoser]
    [MeanColumn, MedianColumn, MaxColumn, MinColumn]
    public class MessageBenchmark
    {
        private NetServer _server;
        private NetClient _client;
        private byte[] _payload;
        private TaskCompletionSource<bool> _tcs;
        private readonly string _ip = "127.0.0.1";
        private readonly int _port = 55555;

        [Params(100, 300, 500)]
        public int MessageSize;

        [GlobalSetup]
        public void Setup()
        {
            _payload = new byte[MessageSize];
            new Random(42).NextBytes(_payload);

            _server = new NetServer();
            _server.OnClientConnected += c => {};
            _server.OnClientDataReceived += (c, data) => c.Send(data.ToArray()); // Echo server
            _server.OnError += e => Console.WriteLine(e);
            _server.Start(_ip, _port);

            _client = new NetClient();
            _client.OnDataReceived += (data) => _tcs?.TrySetResult(true);
            _client.OnError += e => Console.WriteLine(e);
            _client.Connect(_ip, _port);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _client.Stop();
            _server.Stop();
            _client.Dispose();
            _server.Dispose();
        }

        [Benchmark]
        public async Task SendAndReceive()
        {
            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _client.Send(_payload);
            await _tcs.Task;
        }
    }
} 