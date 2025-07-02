# Miku

High performance TCP server/client library

## Features
- Asynchronous I/O based TCP server/client
- Accepting Middlewares to handle incoming and outgoing data
- Zero-allocation ring buffers for high-throughput streaming
- Event-driven, non-blocking operations with back-pressure support
- Built-in performance metrics: messages/sec, MB/sec, latency
- Cross-platform support: .NET Standard 2.1, .NET 6.0, .NET 8.0
- Extensible pipeline for framing, compression, encryption

## Performance Testing

The project includes a unified performance test tool measures "messages per second" and throughput for different scenarios:

- **Single unified application** with server and client commands
- **Multiple test scenarios**: Echo, broadcast, silent (server) / burst, sustained, latency (client)
- **Configurable parameters**: Message sizes, rates, duration, client counts
- **Real-time performance metrics**: Messages/sec, MB/sec, round-trip times

### Quick Start
```bash
# Run server
dotnet run --project Miku.PerformanceTest/Miku.PerformanceTest.csproj server

# Run client (in another terminal)
dotnet run --project Miku.PerformanceTest/Miku.PerformanceTest.csproj client --size 100 --rate 100 --duration 30
```

See [Miku.PerformanceTest/README.md](Miku.PerformanceTest/README.md) for detailed usage instructions and performance testing scenarios.

## Documentation

### NetClient

NetClient allows you to connect to a TCP server, send/receive raw byte data, and track connection lifecycle.

#### Constructors
- `NetClient()`: Create a new client instance.

#### Connection
- `void Connect(string ip, int port, int bufferSize = 1024)`: Connect to a server at the given IP and port. Optionally specify receive buffer size.
- `bool IsConnected { get; }`: Indicates if the client is connected.
- `string Ip { get; }`: Remote endpoint IP.

#### Data Transfer
- `void Send(ReadOnlyMemory<byte> data)`: Sends raw data to the server.
- `event Action<ReadOnlyMemory<byte>> OnDataReceived`: Raised when data is received from the server.

#### Events & Lifecycle
- `event Action OnConnected`: Raised upon successful connection.
- `event Action<string> OnDisconnected`: Raised when the client disconnects, with reason.
- `event Action<Exception> OnError`: Raised on errors.
- `void Stop(string reason = "Connection closed by client", bool includeCallStack = false)`: Gracefully stops the client.

#### Properties
- `int Id { get; }`: Unique identifier for this client instance.
- `int BufferSize { get; }`: Configured receive buffer capacity.
- `NetClientStats Stats { get; }`: Performance statistics (messages sent/received, bytes sent/received, drops).
- `bool HasPendingSends { get; }`: Whether there are pending outgoing messages.
- `int PendingSendCount { get; }`: Number of messages queued for sending.

#### Utilities
- `void ResetStats()`: Reset all performance counters to zero.
- `string GetDiagnosticInfo()`: Retrieve detailed diagnostic information (buffer usage, stats, connection state).

### NetServer

NetServer listens for incoming TCP connections and dispatches data to subscribed clients.

#### Configuration
- `int BufferSize { get; set; }`: Size of send/receive buffer (default: 64 KiB).

#### Lifecycle
- `void Start(string ip, int port, int backLog = 1000)`: Begin listening on the specified IP and port.
- `void Stop()`: Stop listening and disconnect all clients.
- `bool IsListening { get; }`: Indicates if server is active.
- `int ClientCount { get; }`: Current number of connected clients.

#### Events
- `event Action<NetClient> OnClientConnected`: Triggered when a client connects.
- `event Action<NetClient, string> OnClientDisconnected`: Triggered when a client disconnects, with reason.
- `event Action<NetClient, ReadOnlyMemory<byte>> OnClientDataReceived`: Triggered when a client sends data.
- `event Action<Exception> OnError`: Triggered on server errors.

### Middleware

Implement `INetMiddleware` to process data before sending or after receiving:

```csharp
public interface INetMiddleware
{
    void ProcessSend(ReadOnlyMemory<byte> src, ArrayBufferWriter<byte> dst);
    (bool halt, int consumedFromOrigin) ProcessReceive(ReadOnlyMemory<byte> src, ArrayBufferWriter<byte> dst);
}
```

- `ProcessSend`: Transform or wrap outgoing data. Write to `dst`.
- `ProcessReceive`: Transform or unwrap incoming data. Return `(halt: true, consumed)` to buffer until ready. You should only `halt` when fails to process incoming data. For `consumed`, you should return a number of consumed bytes **related** to the original buffer (e.g. what you originally received from the socket).

**Built-in middleware examples:**
- `PacketFrameMiddleware`: Prepends a 4-byte length header for framing.
- `Lz4CompressionMiddleware`: Compresses/decompresses data with LZ4.

### Examples

#### Echo Server and Client

```csharp
// Server
var server = new NetServer();
server.OnClientConnected += c => Console.WriteLine($"Client connected: {c.Ip}");
server.OnClientDataReceived += (c, data) => c.Send(data.ToArray());
server.OnError += e => Console.WriteLine(e);
server.Start("0.0.0.0", 54323);

// Client
var client = new NetClient();
client.OnDataReceived += data =>
{
    Console.WriteLine($"Echoed: {string.Join(',', data.ToArray())}");
    client.Stop();
    server.Stop();
};
client.Connect("127.0.0.1", 54323);
client.Send(new byte[]{1,2,3,4,5});
```

#### Framing Middleware

```csharp
// Server
var server = new NetServer();
server.OnClientConnected += c => c.AddMiddleware(new PacketFrameMiddleware());
server.OnClientDataReceived += (c, data) => c.Send(data.ToArray());
server.Start("0.0.0.0", 54324);

// Client
var client = new NetClient();
client.AddMiddleware(new PacketFrameMiddleware());
client.OnDataReceived += data => Console.WriteLine($"Received: {string.Join(',', data.ToArray())}");
client.Connect("127.0.0.1", 54324);
client.Send(new byte[]{1,2,3,4,5});
```

#### Compression + Framing Middleware

```csharp
var server = new NetServer();
server.OnClientConnected += c =>
{
    c.AddMiddleware(new Lz4CompressionMiddleware());
    c.AddMiddleware(new PacketFrameMiddleware());
};
server.OnClientDataReceived += (c, data) => c.Send(data.ToArray());
server.Start("0.0.0.0", 54324);

var client = new NetClient();
client.AddMiddleware(new Lz4CompressionMiddleware());
client.AddMiddleware(new PacketFrameMiddleware());
client.OnDataReceived += data => Console.WriteLine($"Received: {string.Join(',', data.ToArray())}");
client.Connect("127.0.0.1", 54324);
client.Send(new byte[]{1,2,3,4,5});
```

#### Ping-Pong Protocol

```csharp
// Server: deserialize Ping, reply with Pong
server.OnClientConnected += c =>
{
    c.AddMiddleware(new Lz4CompressionMiddleware());
    c.AddMiddleware(new PacketFrameMiddleware());
};
server.OnClientDataReceived += (c, data) =>
{
    Deserializer.Deserialize(data.Span, out IProtocol msg);
    if (msg is Ping ping)
    {
        // validate ping
        c.Send(pong.Serialize());
    }
};

// Client
var client = new NetClient();
client.AddMiddleware(new Lz4CompressionMiddleware());
client.AddMiddleware(new PacketFrameMiddleware());
client.OnDataReceived += data =>
{
    Deserializer.Deserialize(data.Span, out IProtocol msg);
    if (msg is Pong) Console.WriteLine("Received Pong!");
    client.Stop();
    server.Stop();
};
client.Connect("127.0.0.1", 54324);
client.Send(ping.Serialize());
```