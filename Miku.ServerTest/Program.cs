using System;
using Miku.Core;
using System.Threading;
using System.Collections.Generic;

namespace Miku.ServerTest
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            //total byte received from clients
            ulong total = 0;
            //all clients connected
            List<uint> clients = new List<uint>();
            //server application
            Server server = new Server(1333);
            //use packet (this is true by default)
            server.UsePacket = true;
            //set max buffer length (30KB by default)
            server.MaxBufferSize = 1024 * 30;
            //on connect callback
            server.OnConnect += id =>
            {
                Console.WriteLine($"[{id}] is now connected");
                lock (clients)
                {
                    clients.Add(id);
                }
            };
            //on received callback
            server.OnMessage += (id, message) =>
            {
                //HERE WE USED PACKET IN CLIENTS AS WELL, SO WE NEED TO PARSE IT
                Packet packet = new Packet(message);
                //REMEMBER YOU MIGHT HAVE MULTIPLE PACKETS FROM ONE MESSAGE, YOU NEED TO ITERATE
                //WE CAN USE FOREACH TO RETRIEVE ALL PACKETS FROM ANY PACKET (AS PACKETS ARE IN CHAIN)
                foreach (var p in packet)
                {
                    //GET THE ACTUAL DATA FROM THE PACKET
                    var data = p.Data;
                    //we want to record how much we received in this test
                    Interlocked.Add(ref total, (ulong)data.Count);
                    //and we want to broadcast to all clients we had
                    int cnt = clients.Count;
                    for (int i = 0; i < cnt; i++)
                    {
                        if (i >= clients.Count) break;
                        //broadcast the received data to all clients (will automatically convert data to packet if you enabled server.UsePacket)
                        server.SendToClient(clients[i], data);
                    }
                }
                //OR YOU CAN USE WHILE LOOP ALTERNATIVELY:
                /*
                while (packet.Valid) //CHECK VALIDITY
                {
                    //your logic here
                    myCallback(packet.Data);
                    //AS WE MIGHT HAVE MORE THAN ONE PACKET FROM THIS MESSAGE, WE NEED TO READ THE NEXT PACKET
                    packet = packet.NextPacket;
                }
                */

                //N.B. If the client does not send a packet, you can process message on your own straight ahead, it's just a ArraySegment<byte>!
            };
            //on disconnect callback
            server.OnDisconnect += (id, msg) =>
            {
                Console.WriteLine($"[{id}] has been disconnected: {msg}");
                lock (clients)
                {
                    clients.Remove(id);
                }
            };
            //START THE SERVER
            server.Start();
            //HOLD THE APPLICATION
            while (true)
            {
                Console.WriteLine(
                    $"There are {clients.Count} clients in total, and received {total} bytes since start");
                Thread.Sleep(1000);
            }
        }
    }
}