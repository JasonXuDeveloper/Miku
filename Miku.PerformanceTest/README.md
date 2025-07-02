# Miku Performance Test Tool

A unified console application for measuring the performance of the Miku networking library.

This tool addresses [Issue #1](https://github.com/JasonXuDeveloper/Miku/issues/1) by providing comprehensive performance testing capabilities to measure "messages per second" for different message sizes and scenarios.

## Features

- **Single unified application** with server and client commands
- **Multiple test scenarios**: Echo, broadcast, silent (server) / burst, sustained, latency (client)
- **Real-time performance metrics**: Messages/sec, MB/sec, round-trip times
- **Configurable parameters**: Message sizes, rates, duration, client counts
- **Interactive server commands**: Change modes, reset metrics, view detailed stats
- **Interactive menu**: Run without any arguments to launch an interactive prompt for Server or Client modes
- **Live controls**: Press `R` to reset the current metrics during a live test

## Installation & Build

```bash
# Build the application
dotnet build Miku.PerformanceTest/Miku.PerformanceTest.csproj

# Or build the entire solution
dotnet build
```

## Usage

The application provides two main commands: `server` and `client`.

### Interactive Mode

Running the tool without any arguments starts an interactive menu to select Server or Client mode and enter parameters.

### Server Mode

Start a performance test server that can operate in different modes:

```bash
# Basic server (default: 127.0.0.1:55550, echo mode)
dotnet run --project Miku.PerformanceTest/Miku.PerformanceTest.csproj server

# Custom server configuration
dotnet run --project Miku.PerformanceTest/Miku.PerformanceTest.csproj server --ip 0.0.0.0 --port 8080 --buffersize 131072 --mode broadcast

# See all server options
dotnet run --project Miku.PerformanceTest/Miku.PerformanceTest.csproj server --help
```

#### Server Options:
- `--ip`: IP address to bind to (default: 127.0.0.1)
- `--port`: Port to listen on (default: 55550)
- `--buffersize`: Buffer size per client connection in bytes (default: 65536)
- `--mode`: Server mode - `echo`, `broadcast`, or `silent` (default: echo)

#### Server Modes:
1. **Echo**: Server echoes received messages back to sender (good for latency testing)
2. **Broadcast**: Server broadcasts every received message to all connected clients
3. **Silent**: Server receives messages but doesn't send responses (pure receive testing)

#### Live Display Controls
During a running test (server or client), press:
- `R` to reset the performance metrics display.

### Client Mode

Run performance tests against a server:

```bash
# Basic client test (100 bytes, 100 msg/s, 30 seconds, 1 client, burst mode)
dotnet run --project Miku.PerformanceTest/Miku.PerformanceTest.csproj client

# Custom client test
dotnet run --project Miku.PerformanceTest/Miku.PerformanceTest.csproj client --size 300 --rate 50 --duration 60 --clients 10 --mode sustained

# See all client options
dotnet run --project Miku.PerformanceTest/Miku.PerformanceTest.csproj client --help
```

#### Client Options:
- `--ip`: Server IP address (default: 127.0.0.1)
- `--port`: Server port (default: 55550)
- `--buffersize`: Receive buffer size in bytes (should be >= message size) (default: 1024)
- `--size`: Message size in bytes (default: 100)
- `--rate`: Messages per second (default: 100)
- `--duration`: Test duration in seconds (default: 30)
- `--clients`: Number of concurrent clients (default: 1)
- `--mode`: Test mode - `burst`, `sustained`, or `latency` (default: burst)

#### Client Test Modes:
1. **Burst**: Sends messages at specified rate from all clients simultaneously
2. **Sustained**: Distributes total message load evenly across clients and time
3. **Latency**: Focuses on round-trip time measurements with delays between messages

## Example Test Scenarios

### 1. Basic Echo Test (100-byte messages as requested in Issue #1)

```bash
# Terminal 1: Start echo server
dotnet run --project Miku.PerformanceTest/Miku.PerformanceTest.csproj server

# Terminal 2: Run client with 100-byte messages
dotnet run --project Miku.PerformanceTest/Miku.PerformanceTest.csproj client --size 100 --rate 100 --duration 30
```

### 2. Broadcast Test with Multiple Clients

```bash
# Terminal 1: Start broadcast server
dotnet run --project Miku.PerformanceTest/Miku.PerformanceTest.csproj server --mode broadcast

# Terminal 2: Run multiple clients
dotnet run --project Miku.PerformanceTest/Miku.PerformanceTest.csproj client --clients 5 --size 300 --rate 500
```

### 3. Latency Testing (300 and 500 bytes as mentioned in the issue)

```bash
# Terminal 1: Start echo server
dotnet run --project Miku.PerformanceTest/Miku.PerformanceTest.csproj server

# Terminal 2: Test different message sizes
dotnet run --project Miku.PerformanceTest/Miku.PerformanceTest.csproj client --mode latency --size 300 --duration 60
dotnet run --project Miku.PerformanceTest/Miku.PerformanceTest.csproj client --mode latency --size 500 --duration 60
```

### 4. High-Throughput Silent Test

```bash
# Terminal 1: Start silent server (no responses)
dotnet run --project Miku.PerformanceTest/Miku.PerformanceTest.csproj server --mode silent

# Terminal 2: Run high-rate sustained test
dotnet run --project Miku.PerformanceTest/Miku.PerformanceTest.csproj client --mode sustained --rate 10000 --clients 10 --size 500 --duration 120
```

## Performance Metrics

### Messages per Second (msg/s)
- Primary metric requested in Issue #1
- Number of individual messages processed per second
- Useful for comparing performance across different message sizes

### Throughput (MB/s)
- Total data throughput in megabytes per second
- Better indicator of bandwidth utilization
- Complements message rate for complete performance picture

### Round-Trip Time (ms)
- Time from sending a message to receiving the response
- Critical for interactive applications
- Only meaningful in echo mode or latency testing

## Contributing

When testing the library performance:

1. Test with different message sizes (100, 300, 500 bytes as requested in Issue #1)
2. Test with various client counts (1, 5, 10, 50+ clients)
3. Test different scenarios (echo, broadcast, sustained vs burst)
4. Consider hardware and network limitations when interpreting results
5. Report results with full context (hardware, network, test parameters)

This addresses [Issue #1](https://github.com/JasonXuDeveloper/Miku/issues/1) by providing a comprehensive, easy-to-use performance testing tool that measures "messages per second" for different message sizes and scenarios as requested.