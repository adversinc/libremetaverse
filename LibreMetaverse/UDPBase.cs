/*
 * Copyright (c) 2006, Clutch, Inc.
 * Original Author: Jeff Cesnik
 * All rights reserved.
 *
 * - Redistribution and use in source and binary forms, with or without 
 *   modification, are permitted provided that the following conditions are met:
 *
 * - Redistributions of source code must retain the above copyright notice, this
 *   list of conditions and the following disclaimer.
 * - Neither the name of the openmetaverse.co nor the names 
 *   of its contributors may be used to endorse or promote products derived from
 *   this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" 
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE 
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE 
 * ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE 
 * LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR 
 * CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF 
 * SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN 
 * CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) 
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE 
 * POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.ObjectPool;
using XmlRpcCore;

namespace OpenMetaverse
{
    /// <summary>
    /// 
    /// </summary>
    public abstract class UDPBase
    {
        // these abstract methods must be implemented in a derived class to actually do
        // something with the packets that are sent and received.
        protected abstract void PacketReceived(UDPPacketBuffer buffer);
        protected abstract void PacketSent(UDPPacketBuffer buffer, int bytesSent);

        // the port to listen on
        protected int udpPort;

        // the remote endpoint to communicate with
        protected IPEndPoint remoteEndPoint = null;

        // the UDP socket
        private Socket udpSocket;

        // the all important shutdownFlag.
        private volatile bool shutdownFlag = true;
        
        // Packet pool for default sized packets
        private readonly ObjectPool<UDPPacketBuffer> _packetPool =
            new DefaultObjectPool<UDPPacketBuffer>(new DefaultPooledObjectPolicy<UDPPacketBuffer>());

        private long MaxReceiveTime;
        private long LastPacketTime;
        private long LastPacketDebug;
        private int BiggestPacket;
        private int NumPackets;

        /// <summary>
        /// Initialize the UDP packet handler in server mode
        /// </summary>
        /// <param name="port">Port to listening for incoming UDP packets on</param>
        protected UDPBase(int port)
        {
            udpPort = port;
        }

        /// <summary>
        /// Initialize the UDP packet handler in client mode
        /// </summary>
        /// <param name="endPoint">Remote UDP server to connect to</param>
        protected UDPBase(IPEndPoint endPoint)
        {
            remoteEndPoint = endPoint;
            udpPort = 0;
        }

        /// <summary>
        /// 
        /// </summary>
        public void Start()
        {
            if (!shutdownFlag) return;

            IPEndPoint ipep = new IPEndPoint(Settings.BIND_ADDR, udpPort);
            udpSocket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    // this udp socket flag is not supported under mono,
                    // so we'll catch the exception and continue
                    const int SIO_UDP_CONNRESET = -1744830452;
                    udpSocket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
                }
                catch (Exception)
                {
                    Logger.DebugLog("UDP SIO_UDP_CONNRESET flag not supported on this platform");
                }
            }

            // On at least Mono 3.2.8, multiple UDP sockets can bind to the same port by default.  This means that
            // when running multiple connections, two can occasionally bind to the same port, leading to unexpected
            // errors as they intercept each others messages.  We need to prevent this.  This is not allowed by
            // default on Windows.
            udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);

            udpSocket.Bind(ipep);
            //Logger.Log($"UDP port opened: {(udpSocket.LocalEndPoint as IPEndPoint).Port}", Helpers.LogLevel.Debug);

            // we're not shutting down, we're starting up
            shutdownFlag = false;

            // kick off an async receive.  The Start() method will return, the
            // actual receives will occur asynchronously and will be caught in
            // AsyncEndRecieve().
            AsyncBeginReceive();
        }

        /// <summary>
        /// 
        /// </summary>
        public void Stop()
        {
            if (shutdownFlag) return;

            // wait indefinitely for a writer lock.  Once this is called, the .NET runtime
            // will deny any more reader locks, in effect blocking all other send/receive
            // threads.  Once we have the lock, we set shutdownFlag to inform the other
            // threads that the socket is closed.
            shutdownFlag = true;

            //var port = (udpSocket?.LocalEndPoint as IPEndPoint)?.Port ?? -1;
            //Logger.Log($"UDP port closed: {port}", 
            //    Helpers.LogLevel.Debug);
            udpSocket.Close();
        }

        /// <summary>
        /// 
        /// </summary>
        public bool IsRunning => !shutdownFlag;

        private void AsyncBeginReceive()
        {
            // allocate a packet buffer
            UDPPacketBuffer buf = _packetPool.Get();

            if (shutdownFlag) return;

            try
            {
                udpSocket.SendTimeout = 1;
                udpSocket.ReceiveTimeout = 1;
                
                // kick off an async read
                udpSocket.BeginReceiveFrom(
                    buf.Data,
                    0,
                    UDPPacketBuffer.DEFAULT_BUFFER_SIZE,
                    SocketFlags.None,
                    ref buf.RemoteEndPoint,
                    AsyncEndReceive,
                    buf);
            }
            catch (SocketException e)
            {
                if (e.SocketErrorCode == SocketError.ConnectionReset)
                {
                    Logger.Log("SIO_UDP_CONNRESET was ignored, attempting to salvage the UDP listener on port " + udpPort, Helpers.LogLevel.Error);
                    bool salvaged = false;
                    while (!salvaged)
                    {
                        try
                        {
                            udpSocket.BeginReceiveFrom(
                                //wrappedBuffer.Instance.Data,
                                buf.Data,
                                0,
                                UDPPacketBuffer.DEFAULT_BUFFER_SIZE,
                                SocketFlags.None,
                                ref buf.RemoteEndPoint,
                                AsyncEndReceive,
                                //wrappedBuffer);
                                buf);
                            salvaged = true;
                        }
                        catch (SocketException) { }
                        catch (ObjectDisposedException) { return; }
                    }

                    Logger.Log("Salvaged the UDP listener on port " + udpPort, Helpers.LogLevel.Info);
                }
            }
            catch (ObjectDisposedException) { }
        }

        private void AsyncEndReceive(IAsyncResult iar)
        {
            // Asynchronous receive operations will complete here through the call
            // to AsyncBeginReceive
            if (shutdownFlag) return;

            // start another receive - this keeps the server going!
            AsyncBeginReceive();

            // get the buffer that was created in AsyncBeginReceive
            // this is the received data
            //WrappedObject<UDPPacketBuffer> wrappedBuffer = (WrappedObject<UDPPacketBuffer>)iar.AsyncState;
            //UDPPacketBuffer buffer = wrappedBuffer.Instance;
            UDPPacketBuffer buffer = (UDPPacketBuffer)iar.AsyncState;

            try
            {
                // get the length of data actually read from the socket, store it with the
                // buffer
                var start = Now();
                buffer.DataLength = udpSocket.EndReceiveFrom(iar, ref buffer.RemoteEndPoint);
                var now = Now();
                if(now - start > MaxReceiveTime) {
                    MaxReceiveTime = now - start;
                }

                if(buffer.DataLength > BiggestPacket) {
                    BiggestPacket = buffer.DataLength;
                }

                if(LastPacketDebug < now - 10*1000) {
                    LastPacketDebug = now;
                    
                    if(LastPacketTime > 0 && now - LastPacketTime > 20*1000) {
                        LogDebug(
                            $"[UDPBase] Packets from {((IPEndPoint) buffer.RemoteEndPoint)} were missing for {now - LastPacketTime} seconds",
                        Helpers.LogLevel.Error);
                    }

                    BiggestPacket = 0;
                    NumPackets = 0;
                }

                NumPackets++;
                LastPacketTime = now;

                // call the abstract method PacketReceived(), passing the buffer that
                // has just been filled from the socket read.
                PacketReceived(buffer);
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            finally { _packetPool.Return(buffer); }
        }

        protected virtual void LogDebug(string str, Helpers.LogLevel level) {
        }

        public void AsyncBeginSend(UDPPacketBuffer buf)
        {
            if (shutdownFlag) return;
            try
            {
                // Profiling heavily loaded clients was showing better performance with
                // synchronous UDP packet sending
                udpSocket.SendTo(
                    buf.Data,
                    0,
                    buf.DataLength,
                    SocketFlags.None,
                    buf.RemoteEndPoint);

                //udpSocket.BeginSendTo(
                //    buf.Data,
                //    0,
                //    buf.DataLength,
                //    SocketFlags.None,
                //    buf.RemoteEndPoint,
                //    AsyncEndSend,
                //    buf);
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        //void AsyncEndSend(IAsyncResult result)
        //{
        //    try
        //    {
        //        UDPPacketBuffer buf = (UDPPacketBuffer)result.AsyncState;
        //        if (!udpSocket.Connected) return;
        //        int bytesSent = udpSocket.EndSendTo(result);

        //        PacketSent(buf, bytesSent);
        //    }
        //    catch (SocketException) { }
        //    catch (ObjectDisposedException) { }
        //}

        long Now() {
           return (DateTime.UtcNow.Ticks - 621355968000000000) / 10000;
        }
    }
}
