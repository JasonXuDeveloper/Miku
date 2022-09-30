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
            ulong total = 0;
            //服务端程序
            Server server = new Server(1333);
            //全部客户端
            List<uint> clients = new List<uint>();
            //连接回调
            server.OnConnect += id =>
            {
                Console.WriteLine($"[{id}] is now connected");
                lock (clients)
                {
                    clients.Add(id);
                }
            };
            //收到客户端消息回调
            server.OnMessage += (id, data) =>
            {
                //标记收到的字节
                Interlocked.Add(ref total, (ulong)data.Count);
                //广播给全部客户端
                int cnt = clients.Count;
                for (int i = 0; i < cnt; i++)
                {
                    if (i >= clients.Count) break;
                    server.SendToClient(clients[i], data);
                }
            };
            //客户端断开回调
            server.OnDisconnect += (id, msg) =>
            {
                Console.WriteLine($"[{id}] has been disconnected: {msg}");
                lock (clients)
                {
                    clients.Remove(id);
                }
            };
            //启动服务端
            server.Start();
            while (true)
            {
                Console.WriteLine($"There are {clients.Count} clients in total, and received {total} bytes since start");
                Thread.Sleep(1000);
            }
        }
    }
}