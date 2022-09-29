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
            //测试数量
            int testCount = 1000;
            
            //总共收到的字节
            ulong total = 0;
            //客户端测试
            new Thread(() =>
            {
                //测试消息
                StringBuilder sb = new StringBuilder();
                sb.Append('1', 100);
                var str = sb.ToString();
                var data = Encoding.Default.GetBytes(str);
                //循环创建客户端
                for (int i = 1; i <= testCount; i++)
                {
                    try
                    {
                        int index = i;
                        Client client = new Client("127.0.0.1", 1333);
                        //连接成功回调
                        client.OnConnected += async () =>
                        {
                            Console.WriteLine($"{index}连上了");
                            //每秒发一次
                            while (true)
                            {
                                await Task.Delay(1000 + index/10).ConfigureAwait(false);
                                await client.Send(data).ConfigureAwait(false);
                            }
                        };
                        //收到服务端消息回调
                        client.OnReceived += message =>
                        {
                            //标记收到的字节
                            Interlocked.Add(ref total, (ulong)message.Count);
                        };
                        //断开连接回调
                        client.OnClose += (msg) => { Console.WriteLine($"{index}断开了: {msg}"); };
                        //连接服务端
                        client.Connect();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"无法继续创建客户端：{ex}");
                        testCount = i;
                        break;
                    }
                }
            }).Start();


            while (true)
            {
                Console.WriteLine($"全部客户端累计共收到{total}个字节");
                Thread.Sleep(1000);
            }
        }
    }
}