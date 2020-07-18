﻿/*
Technitium Library
Copyright (C) 2020  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TechnitiumLibrary.Net.Proxy;

namespace TechnitiumLibrary.Net.Dns.ClientConnection
{
    public class TcpClientConnection : DnsClientConnection
    {
        #region variables

        const int SOCKET_CONNECT_TIMEOUT = 10000;
        const int SOCKET_SEND_TIMEOUT = 15000;
        const int SOCKET_RECEIVE_TIMEOUT = 60000; //to keep connection alive for reuse

        bool _pooled;

        Stream _tcpStream;
        Thread _readThread;

        readonly ConcurrentDictionary<ushort, Transaction> _transactions = new ConcurrentDictionary<ushort, Transaction>();

        readonly MemoryStream _sendBuffer = new MemoryStream(32);
        readonly MemoryStream _recvBuffer = new MemoryStream(64);

        readonly object _tcpStreamLock = new object();

        DateTime _lastQueried;

        #endregion

        #region constructor

        public TcpClientConnection(NameServerAddress server, NetProxy proxy)
            : base(DnsTransportProtocol.Tcp, server, proxy)
        { }

        protected TcpClientConnection(DnsTransportProtocol protocol, NameServerAddress server, NetProxy proxy)
            : base(protocol, server, proxy)
        { }

        #endregion

        #region IDisposable

        bool _disposed;

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing && !_pooled)
            {
                if (_tcpStream != null)
                    _tcpStream.Dispose();
            }

            _disposed = true;
        }

        #endregion

        #region private

        private Stream GetConnection()
        {
            if (_tcpStream != null)
                return _tcpStream;

            Socket socket;

            if (_proxy == null)
            {
                if (_server.IPEndPoint == null)
                    _server.RecursiveResolveIPAddress();

                socket = new Socket(_server.IPEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                socket.ConnectAsync(_server.IPEndPoint, SOCKET_CONNECT_TIMEOUT);
            }
            else
            {
                socket = _proxy.Connect(_server.EndPoint, SOCKET_CONNECT_TIMEOUT);
            }

            socket.SendTimeout = SOCKET_SEND_TIMEOUT;
            socket.ReceiveTimeout = SOCKET_RECEIVE_TIMEOUT;
            socket.SendBufferSize = 512;
            socket.ReceiveBufferSize = 2048;
            socket.NoDelay = true;

            _tcpStream = GetNetworkStream(socket);

            _readThread = new Thread(ReadDnsDatagramAsync);
            _readThread.IsBackground = true;
            _readThread.Start();

            return _tcpStream;
        }

        private void ReadDnsDatagramAsync(object state)
        {
            try
            {
                while (true)
                {
                    //read response datagram
                    DnsDatagram response = new DnsDatagram(_tcpStream, true, _recvBuffer);

                    //signal waiting thread of response
                    if (_transactions.TryGetValue(response.Identifier, out Transaction transaction))
                    {
                        transaction.Stopwatch.Stop();

                        response.SetMetadata(new DnsDatagramMetadata(_server, _protocol, response.Size, transaction.Stopwatch.Elapsed.TotalMilliseconds));

                        transaction.ResponseTask.TrySetResult(response);
                    }
                }
            }
            catch
            { }
            finally
            {
                lock (_tcpStreamLock)
                {
                    if (_tcpStream != null)
                    {
                        _tcpStream.Dispose();
                        _tcpStream = null;
                    }

                    foreach (Transaction transaction in _transactions.Values)
                    {
                        transaction.Stopwatch.Stop();
                        transaction.ResponseTask.SetException(new DnsClientException("Connection was closed."));
                    }

                    _transactions.Clear();
                }
            }
        }

        #endregion

        #region protected

        protected virtual Stream GetNetworkStream(Socket socket)
        {
            return new NetworkStream(socket, true);
        }

        #endregion

        #region public

        internal void SetPooled()
        {
            _pooled = true;
        }

        public override async Task<DnsDatagram> QueryAsync(DnsDatagram request, int timeout)
        {
            try
            {
                Transaction transaction = new Transaction();

                while (!_transactions.TryAdd(request.Identifier, transaction))
                    request.SetRandomIdentifier();

                Task<bool> sendAsyncTask = Task.Run(delegate ()
                {
                    if (!Monitor.TryEnter(_tcpStreamLock, timeout))
                        return false; //timed out

                    try
                    {
                        //get connection
                        Stream tcpStream = GetConnection();

                        transaction.Stopwatch.Start();

                        //send request
                        request.WriteToTcpAsync(tcpStream, _sendBuffer);
                        tcpStream.Flush();

                        return true;
                    }
                    finally
                    {
                        Monitor.Exit(_tcpStreamLock);
                    }
                });

                //wait for request with timeout
                using (var cancellationTokenSource = new CancellationTokenSource())
                {
                    if (await Task.WhenAny(new Task[] { sendAsyncTask, Task.Delay(timeout, cancellationTokenSource.Token) }) != transaction.ResponseTask.Task)
                        return null; //send timed out

                    cancellationTokenSource.Cancel(); //to stop delay task
                }

                if (!await sendAsyncTask)
                    return null; //monitor wait timed out

                //wait for response with timeout
                using (var cancellationTokenSource = new CancellationTokenSource())
                {
                    if (await Task.WhenAny(new Task[] { transaction.ResponseTask.Task, Task.Delay(timeout, cancellationTokenSource.Token) }) != transaction.ResponseTask.Task)
                        return null; //timed out

                    cancellationTokenSource.Cancel(); //to stop delay task
                }

                return await transaction.ResponseTask.Task; //await again for any exception to be rethrown
            }
            catch (IOException)
            {
                //connection is closed, return null. retry attempt will reconnect back.
                return null;
            }
            catch (ObjectDisposedException)
            {
                //connection is closed, return null. retry attempt will reconnect back.
                return null;
            }
            finally
            {
                if (_transactions.TryRemove(request.Identifier, out Transaction transaction))
                    transaction.ResponseTask.TrySetCanceled();

                _lastQueried = DateTime.UtcNow;
            }
        }

        #endregion

        #region properties

        public DateTime LastQueried
        { get { return _lastQueried; } }

        #endregion

        class Transaction
        {
            public readonly TaskCompletionSource<DnsDatagram> ResponseTask = new TaskCompletionSource<DnsDatagram>();
            public readonly Stopwatch Stopwatch = new Stopwatch();
        }
    }
}
