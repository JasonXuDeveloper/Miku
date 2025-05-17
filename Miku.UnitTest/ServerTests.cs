using Miku.Core;
using Miku.UnitTest.NinoGen;
using Miku.UnitTest.Protocol;

namespace Miku.UnitTest;

public class ServerTests
{
    [SetUp]
    public void Setup()
    {
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.WriteLine(e.Exception);
            e.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) => { Console.WriteLine(e.ExceptionObject); };
    }

    [Test]
    public async Task ServerReceiveTest()
    {
        // A data for testing.
        var buffer = new byte[] { 1, 2, 3, 4, 5 };
        // ip port info
        var ip = "0.0.0.0";
        var port = 54321;
        // await for assertion
        TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();

        // Create a new server.
        var server = new NetServer();
        server.OnClientConnected += client => { Console.WriteLine($"Client connected: {client.Ip}"); };
        server.OnClientDisconnected += client =>
        {
            Console.WriteLine($"Client disconnected: {client.Ip}");
            server.Stop();
        };
        server.OnClientDataReceived += (client, data) =>
        {
            Console.WriteLine($"Data received from {client.Ip}: {string.Join(',', data.ToArray())}");
            // Assert the data received from the client is sequentially equal to the buffer.
            tcs.SetResult(data.ToArray().SequenceEqual(buffer));
        };
        server.OnError += exception =>
        {
            Console.WriteLine($"An error occurred: {exception} {exception.StackTrace}");
            throw exception;
        };

        server.Start(ip, port);

        // Simulate a client connecting to the server.
        var client = new NetClient();
        client.Connect(ip, port);

        // Simulate the client sending data to the server.
        client.Send(buffer);

        // Close the client.
        client.Stop();

        // await for the server to stop
        Assert.That(await tcs.Task);

        // Stop the server.
        server.Stop();
    }

    [Test]
    public async Task ServerStopsClientTest()
    {
        // A data for testing.
        var buffer = new byte[] { 1, 2, 3, 4, 5 };
        // ip port info
        var ip = "0.0.0.0";
        var port = 54322;
        // await for assertion
        TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();

        // Create a new server.
        var server = new NetServer();
        server.OnClientConnected += client => { Console.WriteLine($"Client connected: {client.Ip}"); };
        server.OnClientDisconnected += client => { Console.WriteLine($"Client disconnected: {client.Ip}"); };
        server.OnClientDataReceived += (client, data) =>
        {
            Console.WriteLine($"Data received from {client.Ip}: {string.Join(',', data.ToArray())}");
            server.Stop();
        };
        server.OnError += exception =>
        {
            Console.WriteLine($"An error occurred: {exception} {exception.StackTrace}");
            throw exception;
        };

        server.Start(ip, port);

        // Simulate a client connecting to the server.
        var client = new NetClient();
        client.OnDisconnected += () =>
        {
            Console.WriteLine("Client disconnected");
            tcs.SetResult(true);
        };

        client.Connect(ip, port);

        // Simulate the client sending data to the server.
        client.Send(buffer);

        // await for the server to stop
        Assert.That(await tcs.Task);

        // Stop the server.
        server.Stop();
    }

    [Test]
    public async Task EchoTest()
    {
        // A data for testing.
        var buffer = new byte[] { 1, 2, 3, 4, 5 };
        // ip port info
        var ip = "0.0.0.0";
        var port = 54323;
        // await for assertion
        TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();

        // Create a new server.
        var server = new NetServer();
        server.OnClientConnected += client => { Console.WriteLine($"Client connected: {client.Ip}"); };
        server.OnClientDisconnected += client => { Console.WriteLine($"Client disconnected: {client.Ip}"); };
        server.OnClientDataReceived += (client, data) =>
        {
            Console.WriteLine($"Data received from {client.Ip}: {string.Join(',', data.ToArray())}");
            // Send the data back to the client.
            client.Send(data.ToArray());
        };
        server.OnError += exception =>
        {
            Console.WriteLine($"An error occurred: {exception} {exception.StackTrace}");
            throw exception;
        };

        server.Start(ip, port);

        // Simulate a client connecting to the server.
        var client = new NetClient();
        client.OnDataReceived += (data) =>
        {
            Console.WriteLine($"Data received from server: {string.Join(',', data.ToArray())}");
            client.Stop();
            tcs.SetResult(data.ToArray().SequenceEqual(buffer));
        };

        client.Connect(ip, port);

        // Simulate the client sending data to the server.
        client.Send(buffer);

        // await for the server to stop
        Assert.That(await tcs.Task);

        // Stop the server.
        server.Stop();
    }

    [Test]
    public async Task FramingMiddlewareTest()
    {
        // A data for testing.
        var buffer = new byte[] { 1, 2, 3, 4, 5 };
        // ip port info
        var ip = "0.0.0.0";
        var port = 54324;
        // await for assertion
        TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();

        // Create a new server.
        var server = new NetServer();
        server.OnClientConnected += client =>
        {
            Console.WriteLine($"Client connected: {client.Ip}");
            // Add the middleware to the client.
            client.AddMiddleware(new PacketFrameMiddleware());
        };
        server.OnClientDisconnected += client => { Console.WriteLine($"Client disconnected: {client.Ip}"); };
        server.OnClientDataReceived += (client, data) =>
        {
            Console.WriteLine($"Data received from {client.Ip}: {string.Join(',', data.ToArray())}");
            // Send the data back to the client.
            client.Send(data.ToArray());
        };
        server.OnError += exception =>
        {
            Console.WriteLine($"An error occurred: {exception} {exception.StackTrace}");
            throw exception;
        };

        server.Start(ip, port);

        // Simulate a client connecting to the server.
        var client = new NetClient();
        client.OnDataReceived += (data) =>
        {
            Console.WriteLine($"Data received from server: {string.Join(',', data.ToArray())}");
            client.Stop();
            tcs.SetResult(data.ToArray().SequenceEqual(buffer));
        };
        // Add the middleware to the client.
        client.AddMiddleware(new PacketFrameMiddleware());
        client.Connect(ip, port);

        // Simulate the client sending data to the server.
        client.Send(buffer);

        // await for the server to stop
        Assert.That(await tcs.Task);

        // Stop the server.
        server.Stop();
    }

    [Test]
    public async Task MultipleMiddlewareTest()
    {
        // A data for testing.
        var buffer = new byte[] { 1, 2, 3, 4, 5 };
        // ip port info
        var ip = "0.0.0.0";
        var port = 54324;
        // await for assertion
        TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();

        // Create a new server.
        var server = new NetServer();
        server.OnClientConnected += client =>
        {
            Console.WriteLine($"Client connected: {client.Ip}");
            // Add the middleware to the client. When send, lz4 first, then frame
            client.AddMiddleware(new Lz4CompressionMiddleware());
            client.AddMiddleware(new PacketFrameMiddleware());
        };
        server.OnClientDisconnected += client => { Console.WriteLine($"Client disconnected: {client.Ip}"); };
        server.OnClientDataReceived += (client, data) =>
        {
            Console.WriteLine($"Data received from {client.Ip}: {string.Join(',', data.ToArray())}");
            // Send the data back to the client.
            client.Send(data.ToArray());
        };
        server.OnError += exception =>
        {
            Console.WriteLine($"An error occurred: {exception} {exception.StackTrace}");
            throw exception;
        };

        server.Start(ip, port);

        // Simulate a client connecting to the server.
        var client = new NetClient();
        client.OnDataReceived += (data) =>
        {
            Console.WriteLine($"Data received from server: {string.Join(',', data.ToArray())}");
            client.Stop();
            tcs.SetResult(data.ToArray().SequenceEqual(buffer));
        };
        // Add the middleware to the client.
        client.AddMiddleware(new Lz4CompressionMiddleware());
        client.AddMiddleware(new PacketFrameMiddleware());
        client.Connect(ip, port);

        // Simulate the client sending data to the server.
        client.Send(buffer);

        // await for the server to stop
        Assert.That(await tcs.Task);

        // Stop the server.
        server.Stop();
    }

    [Test]
    public async Task PingPongTest()
    {
        // ip port info
        var ip = "0.0.0.0";
        var port = 54324;
        // await for assertion
        TaskCompletionSource<IProtocol> tcs = new TaskCompletionSource<IProtocol>();
        Ping dataToSend = new Ping
        {
            Field1 = 1,
            Field2 = "2",
            Field3 = 3.5f,
            Field4 = 4.5,
            Field5 = 5,
            Field6 = 6,
            Field7 = 7,
            Field8 = Guid.NewGuid()
        };
        Pong dataToReceive = new Pong
        {
            Field1 = 1,
            Field2 = "2",
            Field3 = 3.5f,
            Field4 = 4.5,
            Field5 = 5,
            Field6 = 6,
            Field7 = 7,
            Field8 = Guid.NewGuid()
        };

        // Create a new server.
        var server = new NetServer();
        server.OnClientConnected += client =>
        {
            Console.WriteLine($"Client connected: {client.Ip}");
            // Add the middleware to the client. When send, lz4 first, then frame
            client.AddMiddleware(new Lz4CompressionMiddleware());
            client.AddMiddleware(new PacketFrameMiddleware());
        };
        server.OnClientDisconnected += client => { Console.WriteLine($"Client disconnected: {client.Ip}"); };
        server.OnClientDataReceived += (client, data) =>
        {
            Console.WriteLine($"Data received from {client.Ip}: {string.Join(',', data.ToArray())}");
            // Send the data back to the client.
            Deserializer.Deserialize(data.Span, out IProtocol value);
            switch (value)
            {
                case Ping ping:
                    if (!ping.Equals(dataToSend))
                        throw new Exception("Ping data is not equal to the data sent");
                    client.Send(dataToReceive.Serialize());
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        };
        server.OnError += exception =>
        {
            Console.WriteLine($"An error occurred: {exception} {exception.StackTrace}");
            throw exception;
        };

        server.Start(ip, port);

        // Simulate a client connecting to the server.
        var client = new NetClient();
        client.OnDataReceived += (data) =>
        {
            Console.WriteLine($"Data received from server: {string.Join(',', data.ToArray())}");
            client.Stop();
            Deserializer.Deserialize(data.Span, out IProtocol value);
            tcs.SetResult(value);
        };
        // Add the middleware to the client.
        client.AddMiddleware(new Lz4CompressionMiddleware());
        client.AddMiddleware(new PacketFrameMiddleware());
        client.Connect(ip, port);

        // Simulate the client sending data to the server.
        client.Send(dataToSend.Serialize());

        // await for the server to stop
        Assert.That(await tcs.Task is Pong pong && pong.Equals(dataToReceive));

        // Stop the server.
        server.Stop();
    }
}