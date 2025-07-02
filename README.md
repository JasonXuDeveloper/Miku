# Miku

High performance TCP server/client library

## Features
- Asynchronous I/O based TCP server/client
- Accepting Middlewares to handle incoming and outgoing data

## Performance Testing

The project includes a unified performance test tool built with [ConsoleAppFramework](https://github.com/Cysharp/ConsoleAppFramework) to measure "messages per second" and throughput for different scenarios:

- **Single unified application** with server and client commands
- **Multiple test scenarios**: Echo, broadcast, silent (server) / burst, sustained, latency (client)
- **Configurable parameters**: Message sizes (100-500 bytes), rates, duration, client counts
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
TODO