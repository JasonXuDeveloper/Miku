# Miku

High performance TCP server/client library

> Currently under development, welcome to run the tests!



## Features

| Name                                                         | Completed date |
| ------------------------------------------------------------ | -------------- |
| TCP Server                                                   | Sep 29, 2022   |
| TCP Client                                                   | Sep 29, 2022   |
| Basic event callbacks for both Server and Client             | Sep 29, 2022   |
| Application of ArrayPool to control memory allocation        | Sep 29, 2022   |
| Application of async to ensure concurrencing                 | Sep 29, 2022   |
| Thread safe process and callbacks                            | Sep 29, 2022   |
| Check TCP status and disconnect when unavaliable             | Sep 29, 2022   |
| Packet (optional, ensure reading the correct length of data) | Sep 30, 2022   |
| Encryption (optional, xor)                                   | Oct 3, 2022    |
| Compression (optional, zlib)                                 | N/A            |





## Test

I've provided a test that each client sends 30 bytes to the server per second, and the server broadcast each message from each client to all clients.

Suppose we have 100 clients, then each client would send 30 bytes per second, and receive ```100(number of clients who sent a message per second)*30(size of the message)``` bytes per second. The server would send ```100(number of message received from clients per second)*100(number of clients who needs to receive those messages)*30(size of the message)``` bytes per second, and receive ```100(number of clients)*30(size of the message)``` bytes per second.

> Both server and client test enables packet, which allocates extra 4 bytes per message as a header indicates the length of the message

### Miku.ServerTest

Please run this first to start the server

### Miku.ClientTest

Please run this after starting the server, and it should connect to the server automatically and start sending and receiving messages

> You can also change the testCount in ```Program.cs```, by default there would be 100 clients



## API

TODO



## Limitation

I personally don't recommend testing over 2000 clients broadcasting, as it will allocate an extremely large amount of memory (the allocation will grow exponentially due to the natural of broadcast).

Suppose 2000 clients send to the server a 30 bytes message in one second, then the server needs to send each client ```2000*30``` bytes simutaneously, that means each client connection will allocate an almost ```60KB``` buffer to be able to send the message on time, and as we have 2000 clients, we need around ```2000*60KB``` allocated memory, which is about ```120MB``` of memory (in reality, way more than this!!!). And the more clients, the more memory we need to allocate (and this will definitly grow exponentially).

Throughout my test, broadcasting over 3000 clients (although I don't think anyone needs to broadcast that much clients that often with 30 bytes in a single program) will continually grow the allocation of memory (memory leak).

Let's see the statistics for the provided test: ```n``` number of clients sending 30 bytes to the server per second, and the server broadcast all messages to all clients:

> The memory allocated is how much memory the server program allocated after 10 minutes of running

| Number of clients connected (```n```) | Memory Allocated |
| ------------------------------------- | ---------------- |
| 100                                   | 13.8MB           |
| 200                                   | 14.6MB           |
| 500                                   | 17.5MB           |
| 1000                                  | 21.1MB           |
| 2000 (don't recommend)                | 526.4MB          |

> Since 2000 clients, there is a memory leak (not Miku's fault). It is C#'s implementation on ```Socket.Send```'s fault. As it will keep adding the segment to ```ConcurrentQueueSegment``` with the ```SocketIOEvent```. The reason of it is because we are broadcasting too often, as each client sends a message, the server will broadcast it 2000 times, and we have 2000 clients sending this per second, which means the server needs to broadcast 2000*2000 times per second, and this is why the low level C# code causes a memory leak.
>
> If you don't need to broadcast to that many clients each second, you can run much more clients as there would not be any memory pressure (because the low level code of this project clears the send buffer each millisecond).
>
> So reduce the size of message and reduce the frequency of broadcast, or even reduce the number of clients to broadcast, can significantly increase the concurrency level (number of clients connected at the same time) and improve the memory allocation.
>
> Or if you have to send message very frequent, try combine them then send. (i.e. combine the messages received from different clients per millisecond, and send them all in once), this will significantly reduce the total sending number per millisecond, and can solve the memory leak occured in this test.
>
> **Remember, this test is an extreme pressure test, tradiationally standard application would not need to broadcast to thousands of clients per second**

I strongly recommend use this library to build a ```distributed system```, which is to run multiple servers (as what we call a gateway), then we can redirect these messages (using the compression feature) to the center server (and the center server can send responds to gateways, then redirects to client), therefore by distributing the gateways, we can have a high number of concurrency. (this is what a cluster does)

N.B. Broadcasting to all clients at once is extremely costy to the CPU! A 6 core server can at most broadcast to 3000 clients with a 100-bytes message at once per second. If you need to broadcast to more then 3000 clients per second, and your message has at least 100 bytes, please consider getting a higher quality server (with more cores), or to reduce the message size. You may also apply cluster, or I just recommend don't broadcast to over 3000 clients on one server process (use clusters instead!!! you shouldn't expect one server carries on a service that serves millions of people at the same time!!!)
