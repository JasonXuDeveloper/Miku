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
            int testCount = 100;
            //total number of bytes received across all clients
            ulong totalReceived = 0;
            ulong totalSent = 0;
            //server info
            string ip = "127.0.0.1";
            int port = 1333;
            //test message
            StringBuilder sb = new StringBuilder();
            sb.Append('1', 30);
            var str = sb.ToString();
            var originalData = Encoding.Default.GetBytes(str);
            Console.WriteLine($"original data: {string.Join(",",originalData)}");
            var encryptedData = Encoding.Default.GetBytes(str);
            var encryptKey = new byte[] { 0x01, 0x02, 0x03, 0x04 };
            //WE MIGHT WISH TO ENCRYPT THE DATA TO SEND, IN THIS TEST WE WILL APPLY XOR TO PROTECT OUR MESSAGE
            //you can use extension method, or to call the MessageTool.ApplyXor method directly (you need to provide a key, make sure encrypt key and decrypt key are the same!)
            MessageTool.ApplyXor(encryptedData, encryptKey);
            // data.ApplyXor(new byte[] { 0x01, 0x02, 0x03, 0x04 });//Extension method approach
            //N.B. here we are repeating the same message to all clients, therefore we only need to apply xor to encrypt the message once
            Console.WriteLine($"encrypted data: {string.Join(",",encryptedData)}");
            
            //create clients
            for (int i = 1; i <= testCount; i++)
            {
                try
                {
                    int index = i;
                    //create client, and optionally set max buffer size (for receive, by default this is 30KB)
                    Client client = new Client(ip, port, 1024 * 30);
                    //use packet (this is true by default)
                    client.UsePacket = true;
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
                            //you dont have to use ConfigureAwait(false) when you calling send, this is just slightly faster... (but be aware of threads)
                            await client.SendAsync(encryptedData).ConfigureAwait(false);
                            Interlocked.Add(ref totalSent, (ulong)encryptedData.Length);
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
                            //IF THE SERVER APPLIED XOR BEFORE SENDING THE MESSAGE, WE NEED TO APPLY XOR AGAIN HERE TO DECRYPT
                            //you can use extension method, or to call the MessageTool.ApplyXor method directly (you need to provide a key, make sure encrypt key and decrypt key are the same!)
                            MessageTool.ApplyXor(p.Data, encryptKey);
                            // p.Data.ApplyXor(encryptKey);//Extension method approach
                            
                            //GET DATA INSIDE THE PACKET
                            var pData = p.Data;
                            
                            //here we want to see whether or not the packet feature is accurate
                            if (p.Length != encryptedData.Length || p.Length != pData.Count || !pData.AsSpan().SequenceEqual(originalData))
                                Console.WriteLine(
                                    $"[{index}] Packet data is not 100 bytes or the packet data length is " +
                                    $"not equal to the packet's header length, something went wrong with the packet!\n" +
                                    $"received {pData.Count} bytes: {string.Join(",", pData)}");
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