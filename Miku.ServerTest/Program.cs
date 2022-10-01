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
            ulong totalReceived = 0;
            ulong totalSent = 0;
            //all clients connected
            List<uint> clients = new List<uint>();
            //server application
            Server server = new Server(1333);
            //use packet (this is true by default)
            server.UsePacket = true;
            //set max buffer length (30KB by default) for each client
            server.MaxBufferSize = 1024 * 60;
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
                    Interlocked.Add(ref totalReceived, (ulong)data.Count);
                    //and we want to broadcast to all clients we had
                    int cnt = clients.Count;
                    for (int i = 0; i < cnt; i++)
                    {
                        if (i >= clients.Count) break;
                        uint cid = clients[i];
                        //broadcast the received data to all clients (will automatically convert data to packet if you enabled server.UsePacket)
                        //because we if we blocked the current thread in this callback, it will also block the receive operation (because receive runs on the same thread as the callback thread)
                        //therefore it is recommend to enable merge to send as it would be faster if you send stuffs more than 2000 times per millisecond,
                        //or alternatively you can use send on other threads (but the concurrency is still concerning)
                        //you can send using async (but if you call this too often in one thread, it will still block the current thread, and if you block the callback thread, it will also block the receive operation)
                        // _ = server.SendToClientAsync(cid, data, true);//use await keyword in async context
                        //or sync
                        server.SendToClient(cid, data, true);//if you enable merge to send for the sync method, it might actually become async method
                        Interlocked.Add(ref totalSent, (ulong)data.Count);

                        //send to client has 3 arguments: id, data, and merge to send (this is default as false)
                        //merge the data that needs to send to each client, and send them each ms
                        //only recommend to set this to true when using cluster and need to redirect the message to a central server (as it will be more efficient to the cpu);
                        //or when you need to send data very frequent that will legit block the thread for ages, you can consider to enable merge send
                        //if you only want to send to a remote client, it is not recommend to merge send as it will consume more time to send the message
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
                    $"There are {clients.Count} clients in total, and received {totalReceived} bytes since start, and sent {totalSent} bytes since start");
                Thread.Sleep(1000);
            }
        }
    }
}