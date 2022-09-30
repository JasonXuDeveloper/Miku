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
| Merge data then send (reduce cpu usage)                      | Sep 29, 2022   |
| Check TCP status and disconnect when unavaliable             | Sep 29, 2022   |
| Packet (optional, ensure reading the correct length of data) | Sep 30, 2022   |
| Encryption (optional, xor)                                   | N/A            |
| Compression (optional, zlib)                                 | N/A            |





## Test

I've provided a test that each client sends 100 bytes to the server per second, and the server broadcast each message from each client to all clients.

Suppose we have 1000 clients, then each client would send 100 bytes per second, and receive ```1000(number of clients who sent a message per second)*100(size of the message)``` bytes per second. The server would send ```1000(number of message received from clients per second)*1000(number of clients who needs to receive those messages)*100(size of the message)``` bytes per second, and receive ```1000(number of clients)*100(size of the message)``` bytes per second.

> Both server and client test enables packet, which allocates extra 4 bytes per message as a header indicates the length of the message

### Miku.ServerTest

Please run this first to start the server

### Miku.ClientTest

Please run this after starting the server, and it should connect to the server automatically and start sending and receiving messages

> You can also change the testCount in ```Program.cs```, by default there would be 1000 clients



## API

TODO



## Limitation

I personally don't recommend testing over 3000 clients broadcasting, as it will accuire an extremely large amount of memory.

Suppose 3000 clients send to the server a 100 bytes message in one second, then the server needs to send each client ```3000*100``` bytes simutaneously, which needs to accuire ```3000*3000*100``` bytes (almost 1 GB) of memory per second when it is a bit busy (but not even at peak, at peak this number would be bigger)... 

And the current send buffer strategy would gather a double sized memory each time it is full, throughout my test, running 2000 clients broadcasting will accuire 160MB of memory as an average (at peak maybe 260MB) to be able to broadcast instantly, but 3000 clients will continually grow the allocation of memory (memory leak).

Let's see the statistics for the provided test: ```n``` number of clients sending 100 bytes to the server per second, and the server broadcast all messages to all clients:

| Number of clients (```n```) | Memory Allocated (Avg) | Memory Allocated (Peak) |
| --------------------------- | ---------------------- | ----------------------- |
| 1000                        | 85MB                   | 95MB                    |
| 2000                        | 160MB                  | 180MB                   |

> If you changed the size of message to 30 bytes, I would guarantee you can broatcast it to at least 4000 clients with no memory leak (please let me know if I'm wrong tho).
>
> And if you don't need to broadcast to that many clients each second, you can run much more clients as there would not be any memory pressure (because the low level code of this project clears the send buffer each millisecond).
>
> So reduce the size of message and reduce the frequency of broadcast, or even reduce the number of clients to broadcast, can significantly increase the concurrency level (number of clients connected at the same time)

I strongly recommend use this library to build a ```distributed system```, which is to run multiple servers (as what we call a gateway), then we can redirect these messages (using the compression feature) to the center server (and the center server can send responds to gateways, then redirects to client), therefore by distributing the gateways, we can have a high number of concurrency.

