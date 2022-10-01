using System;
using Miku.Core;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Miku.ClientTest
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            //number of clients to create
            int testCount = 1000;
            //total number of bytes received across all clients
            ulong totalReceived = 0;
            ulong totalSent = 0;
            //server info
            string ip = "127.0.0.1";
            int port = 1333;
            //test message
            StringBuilder sb = new StringBuilder();
            sb.Append('1', 100);
            var str = sb.ToString();
            var data = Encoding.Default.GetBytes(str);

            //create clients
            for (int i = 1; i <= testCount; i++)
            {
                try
                {
                    int index = i;
                    //create client, and optionally set max buffer size (for receive, by default this is 30KB, here we defined it as 50KB)
                    Client client = new Client(ip, port, 1024 * 50);
                    //on connect callback
                    client.OnConnected += async () =>
                    {
                        Console.WriteLine($"{index} is now connected to the server");
                        //here we want to send the message to server each second
                        while (true)
                        {
                            //wait for 1s (with some gaps)
                            await Task.Delay(1000).ConfigureAwait(false);
                            //send message calling client.Send(message, usePacket)
                            //usePacket is true by default (recommended), if you don't want to use it, pass the second argument as false
                            //if usePacket is true, please ensure the onMessage callback in your serverside has parsed packets
                            //you dont have to use ConfigureAwait(false) when you calling send, this is just slightly faster... (but be aware of threads)
                            await client.SendAsync(data).ConfigureAwait(false);
                            Interlocked.Add(ref totalSent, (ulong)data.Length);
                        }

                        //N.B. If the server does not send a packet, you can process message on your own straight ahead, it's just a ArraySegment<byte>!
                    };
                    //on message callback
                    client.OnReceived += message =>
                    {
                        //AS THE SERVER WE CHOSE TO USE PACKETS, WE NEED TO PARSE THE MESSAGE
                        Packet packet = new Packet(message);
                        //REMEMBER YOU MIGHT HAVE MULTIPLE PACKETS FROM ONE MESSAGE, YOU NEED TO ITERATE
                        //WE CAN USE FOREACH TO RETRIEVE ALL PACKETS FROM ANY PACKET (AS PACKETS ARE IN CHAIN)
                        foreach (var p in packet)
                        {
                            //GET DATA INSIDE THE PACKET
                            var pData = p.Data;
                            //here we want to see whether or not the packet feature is accurate
                            if (p.Length != 100 || p.Length != pData.Count)
                                Console.WriteLine(
                                    $"[{index}] Packet data is not 100 bytes or the packet data length is " +
                                    $"not equal to the packet's header length, something went wrong with the packet!");
                            //here we just want to record the total bytes received
                            Interlocked.Add(ref totalReceived, (ulong)pData.Count);
                        }
                        //OR YOU CAN USE WHILE LOOP ALTERNATIVELY:
                        /*
                        while (packet.Valid) //CHECK THE VALIDITY OF THE PACKET
                        {
                            //your logic here
                            myCallback(packet.Data);
                            //AS WE MIGHT HAVE MORE THAN ONE PACKET FROM THE MESSAGE, NEED TO ITERATE TO THE NEXT PACKET
                            packet = packet.NextPacket;
                        }
                        */
                    };
                    //on disconnect callback
                    client.OnClose += (msg) =>
                    {
                        Console.WriteLine($"{index} is now disconnected from the server: {msg}");
                    };
                    //connect to server
                    client.Connect();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"can not continue creating clients: {ex}");
                    break;
                }
            }

            //hold the application
            while (true)
            {
                Console.WriteLine($"All clients received {totalReceived} bytes altogether, send {totalSent} bytes altogether");
                Thread.Sleep(1000);
            }
        }
    }
}