using ConsoleAppFramework;
using Miku.Core;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System;
using System.Linq;

// Interactive entrypoint: if no args provided, launch interactive menu
if (args.Length == 0)
{
    InteractiveMode();
}
else
{
    RunWithArgs(args);
}

void InteractiveMode()
{
    AnsiConsole.MarkupLine("[green]=== Miku Interactive Performance Test ===[/]");
    var role = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("Select mode:")
            .AddChoices("Server", "Client"));
    if (role == "Server")
    {
        var ip = AnsiConsole.Prompt(new TextPrompt<string>("Enter IP:").DefaultValue("127.0.0.1"));
        var port = AnsiConsole.Prompt(new TextPrompt<int>("Enter port:").DefaultValue(55550));
        var serverBufferSize = AnsiConsole.Prompt(new TextPrompt<int>("Enter buffer size (bytes):").DefaultValue(64 * 1024));
        var mode = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select server mode:")
                .AddChoices("echo", "broadcast", "silent"));
        RunWithArgs(new[] { "server", "--ip", ip, "--port", port.ToString(), "--buffersize", serverBufferSize.ToString(), "--mode", mode });
    }
    else
    {
        var ip = AnsiConsole.Prompt(new TextPrompt<string>("Enter IP:").DefaultValue("127.0.0.1"));
        var port = AnsiConsole.Prompt(new TextPrompt<int>("Enter port:").DefaultValue(55550));
        var size = AnsiConsole.Prompt(new TextPrompt<int>("Message size (bytes):").DefaultValue(100));
        var rate = AnsiConsole.Prompt(new TextPrompt<int>("Messages per second:").DefaultValue(100));
        var duration = AnsiConsole.Prompt(new TextPrompt<int>("Duration (seconds):").DefaultValue(30));
        var clientsCount = AnsiConsole.Prompt(new TextPrompt<int>("Number of clients:").DefaultValue(1));
        var clientBufferSize = AnsiConsole.Prompt(new TextPrompt<int>("Enter client buffer size (bytes):").DefaultValue(Math.Max(1024, size * 2)));
        var mode = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select client mode:")
                .AddChoices("burst", "sustained", "latency"));
        RunWithArgs(new[]
        {
            "client", "--ip", ip, "--port", port.ToString(), "--buffersize", clientBufferSize.ToString(), "--size", size.ToString(),
            "--rate", rate.ToString(), "--duration", duration.ToString(), "--clients", clientsCount.ToString(),
            "--mode", mode
        });
    }
}

void RunWithArgs(string[] runArgs)
{
    var app = ConsoleApp.Create();
    app.Add<ServerCommands>();
    app.Add<ClientCommands>();
    app.Run(runArgs);
}

/// <summary>
/// Server commands for performance testing
/// </summary>
public class ServerCommands
{
    private static NetServer? _server;
    private static readonly PerformanceMetrics _metrics = new();
    private static readonly ConcurrentDictionary<int, NetClient> _clients = new();
    private static string _mode = "echo";
    private static readonly object _consoleLock = new();

    /// <summary>
    /// Start performance test server
    /// </summary>
    /// <param name="ip">IP address to bind to</param>
    /// <param name="port">Port to listen on</param>
    /// <param name="bufferSize">Buffer size per client connection in bytes</param>
    /// <param name="mode">Server mode: echo, broadcast, silent</param>
    public async Task Server(string ip = "127.0.0.1", int port = 55550, int bufferSize = 64 * 1024, string mode = "echo")
    {
        _mode = mode.ToLower();

        if (_mode is not ("echo" or "broadcast" or "silent"))
        {
            AnsiConsole.MarkupLine("Invalid mode. Use: echo, broadcast, or silent");
            return;
        }

        AnsiConsole.MarkupLine("=== Miku Performance Test Server ===");
        AnsiConsole.MarkupLine($"IP: {ip}, Port: {port}, Mode: {_mode}");
        AnsiConsole.MarkupLine("==========================================");

        try
        {
            _server = new NetServer();
            _server.BufferSize = bufferSize;
            SetupServerEvents();

            _server.Start(ip, port);
            AnsiConsole.MarkupLine($"Server started on {ip}:{port}");

            // Start live stats display (no runtime commands)
            var table = new Table().AddColumn("Metric").AddColumn("Value");
            AnsiConsole.Live(table)
                .AutoClear(false)
                .Start(ctx =>
                {
                    var process = Process.GetCurrentProcess();
                    var prevCpuTime = process.TotalProcessorTime;
                    var prevTime = DateTime.UtcNow;
                    while (true)
                    {
                        LiveHelper.PromptAndReset(() => _metrics.Reset());
                        LiveHelper.UpdateServerRows(table, _metrics, _clients, _mode, ref process, ref prevCpuTime, ref prevTime);
                        ctx.Refresh();
                        Thread.Sleep(1000);
                    }
                });
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"Error: {ex.Message}");
        }
        finally
        {
            _server?.Stop();
            _server?.Dispose();
            AnsiConsole.MarkupLine("Server stopped.");
            var (received, sent, receivedPerSec, sentPerSec, receivedMBps, sentMBps, elapsed) = _metrics.GetStats();
            AnsiConsole.MarkupLine("=== Final Server Results ===");
            AnsiConsole.MarkupLine($"Received: {received:N0}, Sent: {sent:N0}");
            AnsiConsole.MarkupLine($"Recv/s: {receivedPerSec:F1}, Sent/s: {sentPerSec:F1}");
            AnsiConsole.MarkupLine($"Recv MB/s: {receivedMBps:F2}, Sent MB/s: {sentMBps:F2}");
            AnsiConsole.MarkupLine($"Elapsed: {elapsed:c}");
        }
        await Task.CompletedTask;
    }

    private static void SetupServerEvents()
    {
        _server!.OnClientConnected += client =>
        {
            _clients[client.Id] = client;
            lock (_consoleLock)
            {
                AnsiConsole.MarkupLine($"Client {client.Id} connected from {client.Ip}. Total clients: {_clients.Count}");
            }
        };

        _server.OnClientDisconnected += (client, reason) =>
        {
            _clients.TryRemove(client.Id, out _);
            lock (_consoleLock)
            {
                AnsiConsole.MarkupLine($"Client {client.Id} disconnected: {reason}. Total clients: {_clients.Count}");
            }
        };

        _server.OnClientDataReceived += (client, data) =>
        {
            _metrics.RecordMessageReceived(data.Length);

            switch (_mode)
            {
                case "echo":
                    client.Send(data);
                    _metrics.RecordMessageSent(data.Length);
                    break;

                case "broadcast":
                    var dataArray = data;
                    foreach (var connectedClient in _clients.Values)
                    {
                        try
                        {
                            connectedClient.Send(dataArray);
                            _metrics.RecordMessageSent(data.Length);
                        }
                        catch (Exception ex)
                        {
                            lock (_consoleLock)
                            {
                                AnsiConsole.MarkupLine($"Error broadcasting to client {connectedClient.Id}: {ex.Message}");
                            }
                        }
                    }
                    break;

                case "silent":
                    // Just receive, don't send anything back
                    break;
            }
        };

        _server.OnError += ex =>
        {
            lock (_consoleLock)
            {
                AnsiConsole.MarkupLine($"Server error: {ex.Message}");
            }
        };
    }
}

/// <summary>
/// Client commands for performance testing
/// </summary>
public class ClientCommands
{
    private static readonly List<NetClient> _clients = new();
    private static readonly ClientPerformanceMetrics _metrics = new();
    private static volatile bool _isRunning = true;
    private static readonly object _consoleLock = new();
    private static readonly ConcurrentQueue<DateTime> _sentTimes = new();

    /// <summary>
    /// Run performance test client
    /// </summary>
    /// <param name="ip">Server IP address</param>
    /// <param name="port">Server port</param>
    /// <param name="bufferSize">Receive buffer size in bytes (should be >= message size)</param>
    /// <param name="size">Message size in bytes</param>
    /// <param name="rate">Messages per second</param>
    /// <param name="duration">Test duration in seconds</param>
    /// <param name="clients">Number of concurrent clients</param>
    /// <param name="mode">Test mode: burst, sustained, latency</param>
    public async Task Client(
        string ip = "127.0.0.1",
        int port = 55550,
        int bufferSize = 1024,
        int size = 100,
        int rate = 100,
        int duration = 30,
        int clients = 1,
        string mode = "burst")
    {
        mode = mode.ToLower();

        if (mode is not ("burst" or "sustained" or "latency"))
        {
            AnsiConsole.MarkupLine("Invalid mode. Use: burst, sustained, or latency");
            return;
        }

        AnsiConsole.MarkupLine("=== Miku Performance Test Client ===");
        AnsiConsole.MarkupLine($"Target: {ip}:{port}");
        AnsiConsole.MarkupLine($"Receive Buffer: {bufferSize} bytes, Message Size: {size} bytes");
        AnsiConsole.MarkupLine($"Rate: {rate} msg/s");
        AnsiConsole.MarkupLine($"Duration: {duration} seconds");
        AnsiConsole.MarkupLine($"Clients: {clients}");
        AnsiConsole.MarkupLine($"Mode: {mode}");
        AnsiConsole.MarkupLine("=====================================");

        try
        {
            // Run the test with live stats display
            var testTask = RunTest(ip, port, bufferSize, size, rate, duration, clients, mode);
            var table = new Table().AddColumn("Metric").AddColumn("Value");
            AnsiConsole.Live(table)
                .AutoClear(false)
                .Start(ctx =>
                {
                    var process = Process.GetCurrentProcess();
                    var prevCpuTime = process.TotalProcessorTime;
                    var prevTime = DateTime.UtcNow;
                    while (!testTask.IsCompleted)
                    {
                        LiveHelper.PromptAndReset(() => _metrics.Reset());
                        LiveHelper.UpdateClientRows(table, _metrics, _clients, mode, ref process, ref prevCpuTime, ref prevTime);
                        ctx.Refresh();
                        Thread.Sleep(1000);
                    }
                });
            await testTask;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"Error: {ex.Message}");
        }
        finally
        {
            await DisconnectAll();
            var (sent, received, sentPerSec, receivedPerSec, sentMBps, receivedMBps, avgRoundTripMs, elapsed) = _metrics.GetStats();
            AnsiConsole.MarkupLine("=== Final Client Results ===");
            AnsiConsole.MarkupLine($"Sent: {sent:N0}, Received: {received:N0}");
            AnsiConsole.MarkupLine($"Sent/s: {sentPerSec:F1}, Recv/s: {receivedPerSec:F1}");
            AnsiConsole.MarkupLine($"Sent MB/s: {sentMBps:F2}, Recv MB/s: {receivedMBps:F2}");
            AnsiConsole.MarkupLine($"Avg RTT (ms): {avgRoundTripMs:F1}");
            AnsiConsole.MarkupLine($"Elapsed: {elapsed:c}");
            AnsiConsole.MarkupLine("Client test completed.");
        }
    }

    private static async Task RunTest(string ip, int port, int bufferSize, int messageSize, int messagesPerSecond, int duration, int clientCount, string mode)
    {
        try
        {
            _metrics.Reset();

            // Create and connect clients
            for (int i = 0; i < clientCount; i++)
            {
                var client = new NetClient();
                SetupClientEvents(client);

                try
                {
                    client.Connect(ip, port, bufferSize);
                    _clients.Add(client);

                    if (i < clientCount - 1)
                        await Task.Delay(10);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"Failed to connect client {i + 1}: {ex.Message}");
                    client.Dispose();
                }
            }

            var connectedClients = _clients.Count(c => c.IsConnected);
            AnsiConsole.MarkupLine($"Connected {connectedClients}/{clientCount} clients");

            if (connectedClients == 0)
            {
                AnsiConsole.MarkupLine("No clients connected. Aborting test.");
                return;
            }

            // Start the appropriate test mode
            switch (mode)
            {
                case "burst":
                    await RunBurstTest(messageSize, messagesPerSecond, duration);
                    break;
                case "sustained":
                    await RunSustainedTest(messageSize, messagesPerSecond, duration);
                    break;
                case "latency":
                    await RunLatencyTest(messageSize, duration);
                    break;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"Test error: {ex.Message}");
        }
        finally
        {
            await Task.Delay(1000); // Allow final messages to be processed
            AnsiConsole.MarkupLine("Test completed.");
        }
    }

    private static void SetupClientEvents(NetClient client)
    {
        client.OnConnected += () =>
        {
            lock (_consoleLock)
            {
                AnsiConsole.MarkupLine($"Client {client.Id} connected");
            }
        };

        client.OnDisconnected += reason =>
        {
            lock (_consoleLock)
            {
                AnsiConsole.MarkupLine($"Client {client.Id} disconnected: {reason}");
            }
        };

        client.OnDataReceived += data =>
        {
            _metrics.RecordMessageReceived(data.Length);

            if (_sentTimes.TryDequeue(out var sentTime))
            {
                var roundTripMs = (DateTime.UtcNow - sentTime).TotalMilliseconds;
                _metrics.RecordRoundTrip((long)roundTripMs);
            }
        };

        client.OnError += ex =>
        {
            lock (_consoleLock)
            {
                AnsiConsole.MarkupLine($"Client {client.Id} error: {ex.Message}");
            }
        };
    }

    private static async Task RunBurstTest(int messageSize, int messagesPerSecond, int duration)
    {
        AnsiConsole.MarkupLine($"Running burst test: {messagesPerSecond} msg/s for {duration} seconds");

        var payload = new byte[messageSize];
        new Random(42).NextBytes(payload);

        var stopwatch = Stopwatch.StartNew();
        var endTime = TimeSpan.FromSeconds(duration);
        var interval = TimeSpan.FromMilliseconds(1000.0 / messagesPerSecond);
        var nextSendTime = stopwatch.Elapsed;

        while (stopwatch.Elapsed < endTime && _isRunning)
        {
            var now = stopwatch.Elapsed;
            if (now < nextSendTime)
            {
                await Task.Delay(nextSendTime - now);
            }

            foreach (var client in _clients.Where(c => c.IsConnected))
            {
                try
                {
                    _sentTimes.Enqueue(DateTime.UtcNow);
                    client.Send(payload);
                    _metrics.RecordMessageSent(messageSize);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"Send error on client {client.Id}: {ex.Message}");
                }
            }

            nextSendTime += interval;
        }
    }

    private static async Task RunSustainedTest(int messageSize, int messagesPerSecond, int duration)
    {
        AnsiConsole.MarkupLine($"Running sustained test: {messagesPerSecond} msg/s for {duration} seconds");

        var payload = new byte[messageSize];
        new Random(42).NextBytes(payload);

        var totalMessages = messagesPerSecond * duration;
        var messagesPerClient = totalMessages / Math.Max(1, _clients.Count(c => c.IsConnected));
        var delayBetweenMessages = TimeSpan.FromMilliseconds(1000.0 / messagesPerSecond * _clients.Count(c => c.IsConnected));

        var tasks = _clients.Where(c => c.IsConnected).Select(async client =>
        {
            for (int i = 0; i < messagesPerClient && _isRunning; i++)
            {
                try
                {
                    _sentTimes.Enqueue(DateTime.UtcNow);
                    client.Send(payload);
                    _metrics.RecordMessageSent(messageSize);

                    if (i < messagesPerClient - 1)
                        await Task.Delay(delayBetweenMessages);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"Send error on client {client.Id}: {ex.Message}");
                    break;
                }
            }
        }).ToArray();

        await Task.WhenAll(tasks);
    }

    private static async Task RunLatencyTest(int messageSize, int duration)
    {
        AnsiConsole.MarkupLine($"Running latency test: ping-pong for {duration} seconds");

        var payload = new byte[messageSize];
        new Random(42).NextBytes(payload);

        if (!_clients.Any(c => c.IsConnected))
        {
            AnsiConsole.MarkupLine("No connected clients for latency test");
            return;
        }

        var client = _clients.First(c => c.IsConnected);
        var stopwatch = Stopwatch.StartNew();
        var endTime = TimeSpan.FromSeconds(duration);

        while (stopwatch.Elapsed < endTime && _isRunning)
        {
            try
            {
                _sentTimes.Enqueue(DateTime.UtcNow);
                client.Send(payload);
                _metrics.RecordMessageSent(messageSize);

                await Task.Delay(10);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"Latency test error: {ex.Message}");
                break;
            }
        }
    }

    private static async Task DisconnectAll()
    {
        var disconnectTasks = _clients.Select(client =>
        {
            try
            {
                if (client.IsConnected)
                {
                    client.Stop();
                }
                client.Dispose();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"Error disconnecting client {client.Id}: {ex.Message}");
            }
            return Task.CompletedTask;
        }).ToArray();

        await Task.WhenAll(disconnectTasks);
        _clients.Clear();
    }
}

/// <summary>
/// Performance metrics tracking
/// </summary>
public class PerformanceMetrics
{
    private long _messagesReceived;
    private long _messagesSent;
    private long _bytesReceived;
    private long _bytesSent;
    private readonly object _lock = new();
    private DateTime _startTime = DateTime.UtcNow;

    public void RecordMessageReceived(int bytes)
    {
        Interlocked.Increment(ref _messagesReceived);
        Interlocked.Add(ref _bytesReceived, bytes);
    }

    public void RecordMessageSent(int bytes)
    {
        Interlocked.Increment(ref _messagesSent);
        Interlocked.Add(ref _bytesSent, bytes);
    }

    public void Reset()
    {
        lock (_lock)
        {
            _messagesReceived = 0;
            _messagesSent = 0;
            _bytesReceived = 0;
            _bytesSent = 0;
            _startTime = DateTime.UtcNow;
        }
    }

    public (long received, long sent, double receivedPerSec, double sentPerSec, double receivedMBps, double sentMBps, TimeSpan elapsed) GetStats()
    {
        lock (_lock)
        {
            var elapsed = DateTime.UtcNow - _startTime;
            var totalSeconds = elapsed.TotalSeconds;

            var receivedPerSec = totalSeconds > 0 ? _messagesReceived / totalSeconds : 0;
            var sentPerSec = totalSeconds > 0 ? _messagesSent / totalSeconds : 0;
            var receivedMBps = totalSeconds > 0 ? (_bytesReceived / 1024.0 / 1024.0) / totalSeconds : 0;
            var sentMBps = totalSeconds > 0 ? (_bytesSent / 1024.0 / 1024.0) / totalSeconds : 0;

            return (_messagesReceived, _messagesSent, receivedPerSec, sentPerSec, receivedMBps, sentMBps, elapsed);
        }
    }
}

/// <summary>
/// Client-specific performance metrics
/// </summary>
public class ClientPerformanceMetrics
{
    private long _messagesSent;
    private long _messagesReceived;
    private long _bytesSent;
    private long _bytesReceived;
    private long _roundTripCount;
    private long _totalRoundTripMs;
    private readonly object _lock = new();
    private DateTime _startTime = DateTime.UtcNow;

    public void RecordMessageSent(int bytes)
    {
        Interlocked.Increment(ref _messagesSent);
        Interlocked.Add(ref _bytesSent, bytes);
    }

    public void RecordMessageReceived(int bytes)
    {
        Interlocked.Increment(ref _messagesReceived);
        Interlocked.Add(ref _bytesReceived, bytes);
    }

    public void RecordRoundTrip(long milliseconds)
    {
        Interlocked.Increment(ref _roundTripCount);
        Interlocked.Add(ref _totalRoundTripMs, milliseconds);
    }

    public void Reset()
    {
        lock (_lock)
        {
            _messagesSent = 0;
            _messagesReceived = 0;
            _bytesSent = 0;
            _bytesReceived = 0;
            _roundTripCount = 0;
            _totalRoundTripMs = 0;
            _startTime = DateTime.UtcNow;
        }
    }

    public (long sent, long received, double sentPerSec, double receivedPerSec, double sentMBps, double receivedMBps, double avgRoundTripMs, TimeSpan elapsed) GetStats()
    {
        lock (_lock)
        {
            var elapsed = DateTime.UtcNow - _startTime;
            var totalSeconds = elapsed.TotalSeconds;

            var sentPerSec = totalSeconds > 0 ? _messagesSent / totalSeconds : 0;
            var receivedPerSec = totalSeconds > 0 ? _messagesReceived / totalSeconds : 0;
            var sentMBps = totalSeconds > 0 ? (_bytesSent / 1024.0 / 1024.0) / totalSeconds : 0;
            var receivedMBps = totalSeconds > 0 ? (_bytesReceived / 1024.0 / 1024.0) / totalSeconds : 0;
            var avgRoundTripMs = _roundTripCount > 0 ? (double)_totalRoundTripMs / _roundTripCount : 0;

            return (_messagesSent, _messagesReceived, sentPerSec, receivedPerSec, sentMBps, receivedMBps, avgRoundTripMs, elapsed);
        }
    }
}

public static class LiveHelper
{
    public static void PromptAndReset(Action resetAction)
    {
        if (!Console.KeyAvailable) return;
        var keyInfo = Console.ReadKey(true);
        if (keyInfo.Key == ConsoleKey.R) resetAction();
    }

    public static void AddSystemRows(Table table, ref Process process, ref TimeSpan prevCpuTime, ref DateTime prevTime)
    {
        process.Refresh();
        var curCpuTime = process.TotalProcessorTime;
        var curTime = DateTime.UtcNow;
        var cpuUsage = (curCpuTime - prevCpuTime).TotalMilliseconds
                       / ((curTime - prevTime).TotalMilliseconds * Environment.ProcessorCount) * 100;
        prevCpuTime = curCpuTime;
        prevTime = curTime;
        var memoryMb = process.WorkingSet64 / 1024.0 / 1024.0;

        table.AddRow("CPU (%)", cpuUsage.ToString("F1"));
        table.AddRow("Memory (MB)", memoryMb.ToString("F2"));
        table.AddRow("Press 'R' to reset", string.Empty);
    }

    public static void UpdateServerRows(Table table, PerformanceMetrics metrics, ConcurrentDictionary<int, NetClient> clients, string mode, ref Process process, ref TimeSpan prevCpuTime, ref DateTime prevTime)
    {
        var (received, sent, receivedPerSec, sentPerSec, receivedMBps, sentMBps, elapsed) = metrics.GetStats();
        table.Rows.Clear();
        table.AddRow("Received", received.ToString("N0"));
        table.AddRow("Sent", sent.ToString("N0"));
        table.AddRow("Recv/s", receivedPerSec.ToString("F1"));
        table.AddRow("Sent/s", sentPerSec.ToString("F1"));
        table.AddRow("Recv MB/s", receivedMBps.ToString("F2"));
        table.AddRow("Sent MB/s", sentMBps.ToString("F2"));
        table.AddRow("Clients", clients.Count.ToString());
        table.AddRow("Mode", mode);
        table.AddRow("Uptime", elapsed.ToString(@"hh\:mm\:ss"));
        AddSystemRows(table, ref process, ref prevCpuTime, ref prevTime);
    }

    public static void UpdateClientRows(Table table, ClientPerformanceMetrics metrics, List<NetClient> clients, string mode, ref Process process, ref TimeSpan prevCpuTime, ref DateTime prevTime)
    {
        var (sent, received, sentPerSec, receivedPerSec, sentMBps, receivedMBps, avgRoundTripMs, elapsed) = metrics.GetStats();
        table.Rows.Clear();
        table.AddRow("Sent", sent.ToString("N0"));
        table.AddRow("Received", received.ToString("N0"));
        table.AddRow("Sent/s", sentPerSec.ToString("F1"));
        table.AddRow("Recv/s", receivedPerSec.ToString("F1"));
        table.AddRow("Sent MB/s", sentMBps.ToString("F2"));
        table.AddRow("Recv MB/s", receivedMBps.ToString("F2"));
        table.AddRow("RT(ms)", avgRoundTripMs.ToString("F1"));
        table.AddRow("Clients", clients.Count(c => c.IsConnected).ToString());
        table.AddRow("Mode", mode);
        table.AddRow("Elapsed", elapsed.ToString(@"hh\:mm\:ss"));
        AddSystemRows(table, ref process, ref prevCpuTime, ref prevTime);
    }
}
