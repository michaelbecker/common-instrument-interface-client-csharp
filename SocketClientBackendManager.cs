using System;
using System.Net.Sockets;
using System.Threading;
using System.IO;
using System.Collections.Generic;
using System.Net;
using System.Diagnostics;



namespace TAInstruments.CommonInstrumentInterface
{

    /// <summary>
    /// Handles the socket, and the SYNC | LENGTH | ... | END portion of the protocol.
    /// </summary>
    /// <remarks>
    /// This class's behavior is intended to be linear and simple.
    /// Ctor -> connect -> disconnect -> connect -> etc.
    /// If an unexpected disconnect occurs, this class does NOT retry.
    /// </remarks>
    class SocketClientBackEndManager : IClientBackEndManager, IDisposable
    {
        #region Dispose Logic 

        // Track whether Dispose has been called.
        private bool disposed = false;

        public void Dispose()
        {
            Dispose(true);

            // Use SupressFinalize in case a subclass
            // of this type implements a finalizer.
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            // If you need thread safety, use a lock around these 
            // operations, as well as in your methods that use the resource.
            if (!disposed)
            {
                if (disposing)
                {
                    if (tcpClient != null)
                        tcpClient.Close();
                }

                // Indicate that the instance has been disposed.
                tcpClient = null;
                disposed = true;
            }
        }

        #endregion


        public SocketClientBackEndManager(string serverAddress, CiiClient client)
        {
            //
            //  Don't build the object if we don't have a valid Address.
            //  This code is strictly to allow the framework to check the 
            //  parsing of the string.
            //
            try
            {
                IPAddress dummy = System.Net.IPAddress.Parse(serverAddress);
            }
            catch(ArgumentNullException)
            {
                throw;
            }
            catch (FormatException)
            {
                throw;
            }

            ServerAddress = serverAddress;
            ReadBuffer = new byte[MaxReadBuffer];
            ciiClient = client;

            Sync = new byte[4];
            Sync[0] = (byte)'S';
            Sync[1] = (byte)'Y';
            Sync[2] = (byte)'N';
            Sync[3] = (byte)'C';

            End = new byte[4];
            End[0] = (byte)'E';
            End[1] = (byte)'N';
            End[2] = (byte)'D';
            End[3] = (byte)' ';

            sendMessageLock = new object();
            disconnectRequested = false;
        }


        public byte[] GetLocalAddress()
        {
            IPEndPoint localIpEndPoint;
            try
            {
                localIpEndPoint = tcpClient.Client.LocalEndPoint as IPEndPoint;
                return localIpEndPoint.Address.GetAddressBytes();
            }
            catch (SocketException se)
            {
                ciiClient.SendAsyncError("GetLocalAddress failed with SocketException " + se.ErrorCode);
                return new byte[4];
            }
            catch (ObjectDisposedException)
            {
                ciiClient.SendAsyncError("GetLocalAddress failed - SocketClosed");
                return new byte[4];
            }
        }


        private void ShutdownNetwork()
        {
            if (networkStream != null)
            {
                networkStream.Close();
            }
            if (tcpClient != null)
            {
                tcpClient.Close();
            }
        }


        public bool SendMessage(byte[] Buffer)
        {
            byte[] Length = BitConverter.GetBytes(Buffer.Length);
            bool Success = false;

            try
            {
                lock (sendMessageLock)
                {
                    networkStream.Write(Sync, 0, 4);
                    networkStream.Write(Length, 0, 4);
                    networkStream.Write(Buffer, 0, Buffer.Length);
                    networkStream.Write(End, 0, 4);
                }

                Success = true;
            }
            catch (ObjectDisposedException)
            {
                ciiClient.SendAsyncError("SendMessage failed - SocketClosed");
                ShutdownNetwork();
            }
            catch (SocketException se)
            {
                ciiClient.SendAsyncError("SendMessage failed with SocketException " + se.ErrorCode);
                ShutdownNetwork();
            }
            catch (IOException ex)
            {
                SocketException se = ex.InnerException as SocketException;
                if (se == null)
                {
                    ciiClient.SendAsyncError("SendMessage failed with IOException");
                }
                else
                {
                    ciiClient.SendAsyncError("SendMessage failed with IOException, inner SocketException " + se.ErrorCode);
                }
                ShutdownNetwork();
            }

            return Success;
        }


        #region Socket Connection code


        public bool Connect()
        {
            disconnectRequested = false;

            try
            {
                tcpClient = new TcpClient(ServerAddress, ServerConnectionPort);
            }
            catch (SocketException se)
            {
                ciiClient.SendAsyncError("Connect failed with SocketException " + se.ErrorCode);
                return false;
            }

            if (sendTimeout > 0)
            {
                tcpClient.SendTimeout = sendTimeout;
            }
            if (receiveTimeout > 0)
            {
                tcpClient.ReceiveTimeout = receiveTimeout;
            }

            networkStream = tcpClient.GetStream();
            readerThread = new Thread(ReaderThread);
            readerThread.Name = "ReaderThread";
            readerThread.Priority = ThreadPriority.Highest;
            readerThread.IsBackground = true; 
            readerThread.Start();

            return true;
        }


        public void Disconnect()
        {
            disconnectRequested = true;
            ShutdownNetwork();

            //
            //  Wait for the reader to clean up.
            //
            if (readerThread != null)
            {
                bool success = readerThread.Join(500);
                if (!success)
                {
                    readerThread.Abort();
                    readerThread.Join();
                }
            }
            tcpClient = null;
            networkStream = null;
            readerThread = null;
        }


        /// <summary>
        /// This Event will only be fired if the socket is unexpectedly disconnected 
        /// due to an exception or error.
        /// </summary>
        public event DisconnectEventHandler AsyncDisconnectEvent;


        #endregion


        #region Private members

        private TcpClient tcpClient;
        private string ServerAddress;
        private const int ServerConnectionPort = 8080;
        private const int MaxReadBuffer = 10 * 1024 * 1024;
        private NetworkStream networkStream;
        private Thread readerThread;
        private byte[] ReadBuffer;
        private CiiClient ciiClient;
        private readonly byte[] Sync;
        private readonly byte[] End;
        /// <summary>
        /// This lock is only used to guarantee that a message is sent
        /// as a contiguous stream of bytes.
        /// </summary>
        private object sendMessageLock;
        private volatile bool disconnectRequested;
        private int sendTimeout;
        private int receiveTimeout;

        #endregion

        public int SendTimeout
        {
            get
            {
                return sendTimeout;
            }
            set
            {
                sendTimeout = value;
                if (tcpClient != null)
                {
                    tcpClient.SendTimeout = sendTimeout;
                }
            }
        }
        public int ReceiveTimeout
        {
            get
            {
                return receiveTimeout;
            }
            set
            {
                receiveTimeout = value;
                if (tcpClient != null)
                {
                    tcpClient.ReceiveTimeout = sendTimeout;
                }
            }
        }



        private bool ReceiveUntilComplete(int LengthToRead)
        {
            int TotalBytesRead = 0;
            int CurBytesRead;

            do
            {
                try
                {
                    CurBytesRead = networkStream.Read(ReadBuffer, TotalBytesRead, LengthToRead);
                    if (CurBytesRead == 0)
                    {
                        ciiClient.SendAsyncError("ReaderThread " + readerThread.ManagedThreadId + " Read shutting down");
                        return false;
                    }
                    TotalBytesRead += CurBytesRead;
                    LengthToRead -= CurBytesRead;
                }
                catch (IOException ex)
                {
                    SocketException se = ex.InnerException as SocketException;
                    if (se == null)
                    {
                        ciiClient.SendAsyncError("ReaderThread " + readerThread.ManagedThreadId + " Read failed with IOException");
                    }
                    else
                    {
                        ciiClient.SendAsyncError("ReaderThread " + readerThread.ManagedThreadId + " Read failed with IOException, inner SocketException " + se.ErrorCode);
                    }
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    ciiClient.SendAsyncError("ReaderThread " + readerThread.ManagedThreadId + " Read failed - NetworkStream is closed.");
                    return false;
                }

            } while (LengthToRead > 0);

            return true;
        }


        private void ThreadTeardown()
        {
            try
            {
                if (!disconnectRequested)
                {
                    if (networkStream != null)
                    {
                        networkStream.Close();
                    }
                    if (tcpClient != null)
                    {
                        tcpClient.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                //
                //  We are in tear-down now anyway, we don't care 
                //  about exceptions, since we are going away anyway.
                //
                Debug.WriteLine("Ignoring exception in ThreadTeardown: " + ex.ToString() + " " + readerThread.ManagedThreadId);
            }

            if (!disconnectRequested)
            {
                DisconnectEventHandler h = AsyncDisconnectEvent;
                if (h != null)
                {
                    h(this, new EventArgs());
                }
            }
        }


        private void ReaderThread()
        {
            while (true)
            {
                bool Success = ReceiveUntilComplete(8);
                if (!Success)
                {
                    //
                    //  If this fails, we already sent an AsyncError.
                    //
                    ThreadTeardown();
                    break;
                }

                if ((ReadBuffer[0] != (byte)'S') ||
                    (ReadBuffer[1] != (byte)'Y') ||
                    (ReadBuffer[2] != (byte)'N') ||
                    (ReadBuffer[3] != (byte)'C'))
                {
                    ciiClient.SendAsyncError("ReaderThread " + readerThread.ManagedThreadId + " - Bad SYNC " + ReadBuffer[0] + ReadBuffer[1] + ReadBuffer[2] + ReadBuffer[3]);
                    ThreadTeardown();
                    break;
                }

                int Length = BitConverter.ToInt32(ReadBuffer, 4);
                if ((Length < 4) || (Length > MaxReadBuffer))
                {
                    ciiClient.SendAsyncError("ReaderThread " + readerThread.ManagedThreadId + "- Bad Length " + Length);
                    ThreadTeardown();
                    break;
                }

                Success = ReceiveUntilComplete(Length + 4);
                if (!Success)
                {
                    //
                    //  If this fails, we already sent an AsyncError.
                    //
                    ThreadTeardown();
                    break;
                }

                if ((ReadBuffer[Length + 0] != (byte)'E') ||
                    (ReadBuffer[Length + 1] != (byte)'N') ||
                    (ReadBuffer[Length + 2] != (byte)'D') ||
                    (ReadBuffer[Length + 3] != (byte)' '))
                {

                    ciiClient.SendAsyncError("ReaderThread " + readerThread.ManagedThreadId + "- Bad END "
                        + ReadBuffer[Length + 0] + ReadBuffer[Length + 1] 
                        + ReadBuffer[Length + 2] + ReadBuffer[Length + 3]);

                    ThreadTeardown();
                    break;
                }

                //
                //  Bounce to CII interface now.
                //
                ciiClient.RouteReceivedMessage(ReadBuffer, Length);
            }

            //
            //  Leaving...
            //
            Debug.WriteLine("Exiting SocketClientBackendManager.ReaderThread() - " + readerThread.ManagedThreadId);
        }

    }

}
