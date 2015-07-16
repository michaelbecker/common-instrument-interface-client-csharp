using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Diagnostics;


namespace TAInstruments.CommonInstrumentInterface
{
    public class CiiClient : ICiiClient, IDisposable
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
                    if (loginAcceptEvent != null)
                        loginAcceptEvent.Close();
                    if (asyncErrorEvent != null)
                        asyncErrorEvent.Close();
                }

                // Indicate that the instance has been disposed.
                loginAcceptEvent = null;
                asyncErrorEvent = null;
                disposed = true;
            }
        }

        #endregion

        #region Message Types

        private enum CiiMessageType
        {
            MtUninitialized = 0x0,
            MtGetCommand = 0x20544547,      /* "GET " */
            MtActionCommand = 0x4E544341,   /* "ACTN" */
            MtLogin = 0x4E474F4C,           /* "LOGN" */
            MtAccept = 0x54504341,          /* "ACPT" */
            MtAck = 0x204B4341,             /* "ACK " */
            MtNak = 0x204B414E,             /* "NAK " */
            MtResponse = 0x20505352,        /* "RSP " */
            MtStatus = 0x54415453,          /* "STAT" */
        }

        private readonly byte[] MessageTypeGet;
        private readonly byte[] MessageTypeAction;
        private readonly byte[] BytesLogin;

        #endregion


        #region Async Errors

        public event AsyncErrorEventHandler AsyncErrorEvent;

        public void SendAsyncError(string errorDescription)
        {
            logger.Log(errorDescription, null, 0);

            //
            //  If we aren't connected, filter out superfluous errors.
            //
            if ((connectionState == ConnectionState.Connected) ||
                (connectionState == ConnectionState.WaitingForLogin))
            {
                lock (asyncErrorsLock)
                {
                    asyncErrors.Enqueue(errorDescription);
                    asyncErrorEvent.Set();
                }
            }
        }

        private void AsyncErrorThread()
        {
            while (true)
            {
                asyncErrorEvent.WaitOne();

                lock(asyncErrorsLock)
                {
                    while (asyncErrors.Count != 0)
                    {
                        string errorString = asyncErrors.Dequeue();
                        AsyncErrorEventHandler e = AsyncErrorEvent;
                        if (e != null)
                        {
                            e(this, new CiiAsyncErrorEventArgs(errorString));
                        }
                    }
                }

            }
        }

        #endregion


        #region Private variables

        private CiiMessagesInFlight messagesInFlight;
        private volatile CiiAccessLevel grantedAccess;
        private AutoResetEvent loginAcceptEvent;
        private AutoResetEvent asyncErrorEvent;
        private const int LoginTimeout = 10000; // in ms

        private Queue<string> asyncErrors;
        private object asyncErrorsLock;
        private Thread asyncErrorThread;
        private Logger logger;

        private enum ConnectionState
        {
            NotConnected,
            WaitingForLogin,
            Connected,
            DisconnectInProgress,
        }
        private volatile ConnectionState connectionState;

        #endregion

        #region Accessors

        public CiiAccessLevel GrantedAccess
        {
            get
            {
                return grantedAccess;
            }
        }
        public bool IsConnected
        {
            get
            {
                return connectionState == ConnectionState.Connected;
            }
        }

        #endregion

        public CiiClient()
        {
            logger = Logger.Instance;
            StatusCallbacks = new Dictionary<uint, ReceiveStatusHandler>();
            statusCallbacksLock = new object();

            loginAcceptEvent = new AutoResetEvent(false);
            messagesInFlight = new CiiMessagesInFlight();
            connectionState = ConnectionState.NotConnected;

            //
            //  Prebuild the Communications arrays
            //
            MessageTypeGet = BitConverter.GetBytes((uint)CiiMessageType.MtGetCommand);
            MessageTypeAction = BitConverter.GetBytes((uint)CiiMessageType.MtActionCommand);
            BytesLogin = BitConverter.GetBytes((uint)CiiMessageType.MtLogin);

            asyncErrorEvent = new AutoResetEvent(false);
            asyncErrors = new Queue<string>();
            asyncErrorsLock = new object();
            asyncErrorThread = new Thread(AsyncErrorThread);
            asyncErrorThread.IsBackground = true;
            asyncErrorThread.Priority = ThreadPriority.AboveNormal;
            asyncErrorThread.Name = "AsyncErrorThread";
            asyncErrorThread.Start();
        }


        /// <summary>
        /// This is the heart of the CII.  This takes data blobs from the backend manager
        /// and interprets it according to the spec.
        /// </summary>
        /// <remarks>
        /// This is called by the backend connected to us.  It needs to know about 
        /// the CiiClient.  This always runs on a worker thread internal to this
        /// library.  A user should never call this.  
        /// </remarks>
        /// <param name="buffer">The byte array where our data lives.</param>
        /// <param name="dataLength">Length valid data in the array.  (dataLength != buffer.Length)</param>
        public void RouteReceivedMessage(byte[] buffer, int dataLength)
        {
            uint sequenceNumber;
            uint statusCode;
            uint subcommand;
            CiiMessageTracker messageTracker;

            uint substatus;

            CiiMessageType type = (CiiMessageType)BitConverter.ToUInt32(buffer, 0);

            switch (type)
            {
                case CiiMessageType.MtAccept:
                    logger.Log("ACCEPT", buffer, dataLength);
                    grantedAccess = (CiiAccessLevel)BitConverter.ToInt32(buffer, 4);
                    loginAcceptEvent.Set();
                    break;


                case CiiMessageType.MtAck:
                    logger.Log("ACK", buffer, dataLength);
                    sequenceNumber = BitConverter.ToUInt32(buffer, 4);

                    messageTracker = messagesInFlight.Retrieve(sequenceNumber);
                    if (messageTracker == null)
                    {
                        SendAsyncError("Protocol Failure - Unexpected ACK");
                        break;
                    }

                    if (messageTracker.AckReceived)
                    {
                        //
                        //  Error!  Double ACK!
                        //
                        messagesInFlight.Delete(sequenceNumber);
                        SendAsyncError("Protocol Failure - Double ACK");
                        break;
                    }
                    else
                    {
                        messageTracker.AckReceived = true;
                    }

                    if ((messageTracker.Completion != null) && (messageTracker.Completion.AckHandler != null))
                    {
                        messageTracker.Completion.AckHandler(
                           messageTracker.Completion.UserData,
                           sequenceNumber);
                    }
                    else
                    {
                        Debug.WriteLine("Discarding ACK for Sequence # " + sequenceNumber);
                    }

                    break;


                case CiiMessageType.MtNak:
                    logger.Log("NAK", buffer, dataLength);
                    sequenceNumber = BitConverter.ToUInt32(buffer, 4);
                    statusCode = BitConverter.ToUInt32(buffer, 8);

                    messageTracker = messagesInFlight.Retrieve(sequenceNumber);
                    if (messageTracker == null)
                    {
                        SendAsyncError("Protocol Failure - Unexpected NAK");
                        break;
                    }

                    messagesInFlight.Delete(sequenceNumber);

                    if (messageTracker.AckReceived)
                    {
                        //
                        //  Error!  ACK / NAK!
                        //
                        SendAsyncError("Protocol Failure - ACK - NAK");
                        break;
                    }

                    if ((messageTracker.Completion != null) && (messageTracker.Completion.NakHandler != null))
                    {
                        messageTracker.Completion.NakHandler(
                           messageTracker.Completion.UserData,
                           sequenceNumber, 
                           statusCode);
                    }
                    else
                    {
                        Debug.WriteLine("Discarding NAK for Sequence # " + sequenceNumber);
                    }

                    messageTracker = null;
                    break;


                case CiiMessageType.MtResponse:
                    logger.Log("RSP", buffer, dataLength);
                    sequenceNumber = BitConverter.ToUInt32(buffer, 4);
                    subcommand = BitConverter.ToUInt32(buffer, 8);
                    statusCode = BitConverter.ToUInt32(buffer, 12);

                    messageTracker = messagesInFlight.Retrieve(sequenceNumber);
                    if (messageTracker == null)
                    {
                        SendAsyncError("Protocol Failure - Unexpected RSP");
                        break;
                    }

                    messagesInFlight.Delete(sequenceNumber);

                    if (!messageTracker.AckReceived)
                    {
                        //
                        //  Error!  No ACK!
                        //
                        SendAsyncError("Protocol Failure - Missing ACK");
                        break;
                    }

                    if ((messageTracker.Completion != null) && (messageTracker.Completion.ResponseHandler != null))
                    {
                       messageTracker.Completion.ResponseHandler(   messageTracker.Completion.UserData,
                                                                    sequenceNumber,
                                                                    subcommand,
                                                                    statusCode,
                                                                    buffer,
                                                                    16,
                                                                    dataLength - 16);
                    }
                    else
                    {
                        Debug.WriteLine("Discarding RSP for Sequence # " + sequenceNumber);
                    }

                    break;


                case CiiMessageType.MtStatus:

                    logger.Log("STAT", buffer, dataLength);

                    substatus = BitConverter.ToUInt32(buffer, 4);

                    if (connectionState != ConnectionState.Connected)
                    {
                        Debug.WriteLine("Throwing away early status message");
                        break;
                    }

                    lock (statusCallbacksLock)
                    {
                        ReceiveStatusHandler callback;
                        StatusCallbacks.TryGetValue(substatus, out callback);

                        if ((callback != null) && (dataLength >= 8))
                        {
                            callback(substatus, buffer, 8, dataLength - 8);
                        }
                        else if ((UnhandledStatusCallback != null) && (dataLength >= 8))
                        {
                            UnhandledStatusCallback(substatus, buffer, 8, dataLength - 8);
                        }
                    }
                    break;

                //
                //  We should never see another type of message here.
                //  This is an asymetric protocol between client and server.
                //
                default:
                    logger.Log("UNKNOWN", buffer, dataLength);
                    SendAsyncError("Unknown MessageType! " + type.ToString());
                    break;
            }
        }


        #region Connect / Disconnect

        /// <summary>
        /// Client invoked code.
        /// </summary>
        /// <param name="requestedAccess"></param>
        /// <returns></returns>
        public bool Connect(CiiAccessLevel requestedAccess)
        {
            if (connectionState != ConnectionState.NotConnected)
            {
                return false;
            }

            bool success = BackEndManager.Connect();

            if (success)
            {
                connectionState = ConnectionState.WaitingForLogin;
                success = Login(requestedAccess);

                if (success)
                {
                    connectionState = ConnectionState.Connected;
                    ConnectEventHandler callbacks = ConnectEvent;
                    if (callbacks != null)
                    {
                        callbacks(this, new EventArgs());
                    }
                }
                else
                {
                    BackEndManager.Disconnect();
                    connectionState = ConnectionState.NotConnected;
                }
            }

            return success;
        }


        /// <summary>
        /// Client invoked code.
        /// </summary>
        public void Disconnect()
        {
            if (connectionState == ConnectionState.Connected)
            {
                connectionState = ConnectionState.DisconnectInProgress;

                messagesInFlight.Clear();

                BackEndManager.Disconnect();

                connectionState = ConnectionState.NotConnected;

                DisconnectEventHandler callbacks = DisconnectEvent;
                if (callbacks != null)
                {
                    callbacks(this, new EventArgs());
                }
            }
        }


        public event ConnectEventHandler ConnectEvent;
        public event DisconnectEventHandler DisconnectEvent;
        public event DisconnectWarningEventHandler DisconnectWarning;
        public event DisconnectErrorEventHandler DisconnectError;

        private int warningDelay = 5;
        private int errorDelay = 30;

        public void SetCommFailureTimeouts(int warningDelay, int errorDelay)
        {
            if (warningDelay <= 0)
            {
                return;
            }

            if (errorDelay <= warningDelay)
            {
                return;
            }

            this.warningDelay = warningDelay;
            this.errorDelay = errorDelay;
        }


        //
        //  This is running on the existing ReaderThread...
        //
        private void AsyncUnexpectedDisconnectHandler(object sender, EventArgs e)
        {
            messagesInFlight.Clear();

            if (connectionState != ConnectionState.Connected)
            {
                //
                //  If we haven't established a good connection, DON'T try 
                //  to recover!!!
                //
                Debug.WriteLine(
                    "CiiClient.AsyncUnexpectedDisconnectHandler() leaving early, not connected." 
                    + connectionState.ToString());
                return;
            }

            DisconnectEventHandler callbacks = DisconnectEvent;
            if (callbacks != null)
            {
                callbacks(this, new EventArgs());
            }

            connectionState = ConnectionState.NotConnected;

            bool Success;
            int delayInMs = 1000;
            bool warningSent = false;
            DateTime start = DateTime.Now;
            TimeSpan errorTimeSpan = TimeSpan.FromSeconds(errorDelay);
            TimeSpan warningTimeSpan = TimeSpan.FromSeconds(warningDelay);

            do
            {
                Debug.WriteLine("CiiClient.AsyncUnexpectedDisconnectHandler waiting " + delayInMs + "ms");

                Thread.Sleep(delayInMs);
                Success = Connect(grantedAccess);
                DateTime end = DateTime.Now;

                if (!Success)
                {
                    TimeSpan diff = end - start;

                    //
                    //  We know that "errorDelay > warningDelay > 0".
                    //
                    if (TimeSpan.Compare(errorTimeSpan, diff) < 0)
                    {
                        Debug.WriteLine(
                            "CiiClient.AsyncUnexpectedDisconnectHandler() dispatching error");

                        DisconnectErrorEventHandler err = DisconnectError;
                        if (err != null)
                        {
                            err(this, new EventArgs());
                        }

                        Debug.WriteLine(
                            "CiiClient.AsyncUnexpectedDisconnectHandler() Aborting retries.");

                        return;
                    }
                    else if (TimeSpan.Compare(warningTimeSpan, diff) < 0)
                    {
                        if (warningSent)
                        {
                            continue;
                        }
                        else
                        {
                            warningSent = true;
                        }

                        Debug.WriteLine(
                            "CiiClient.AsyncUnexpectedDisconnectHandler() dispatching warning.");

                        DisconnectWarningEventHandler warn = DisconnectWarning;
                        if (warn != null)
                        {
                            warn(this, new EventArgs());
                        }
                    }
                }

            } while (!Success);

            Debug.WriteLine("---CiiClient.BackendManagerDisconnectHandler()");
        }


        #endregion


        #region Status Callback code

        /// <summary>
        /// Synchronizes adding Status callbacks with calling them.
        /// </summary>
        private object statusCallbacksLock;
        private Dictionary<uint, ReceiveStatusHandler> StatusCallbacks;
        private ReceiveStatusHandler UnhandledStatusCallback;

        public bool RegisterStatusHandler(uint statusMessage, ReceiveStatusHandler statusDelegate)
        {
            lock (statusCallbacksLock)
            {
                if (StatusCallbacks.ContainsKey(statusMessage))
                {
                    return false;
                }

                StatusCallbacks.Add(statusMessage, statusDelegate);
            }

            return true;
        }


        public bool RegisterUnhandledStatusHandler(ReceiveStatusHandler statusDelegate)
        {
            lock (statusCallbacksLock)
            {
                if (UnhandledStatusCallback != null)
                {
                    return false;
                }

                UnhandledStatusCallback = statusDelegate;
            }

            return true;
        }

        #endregion


        #region Backend Manager Interface

        private IClientBackEndManager backEndManager;
        protected IClientBackEndManager BackEndManager
        {
            //
            //  Only intended to be called once ever.
            //
            set
            {
                Debug.Assert(backEndManager == null);
                backEndManager = value;
                backEndManager.AsyncDisconnectEvent += AsyncUnexpectedDisconnectHandler;
            }
            get
            {
                return backEndManager;
            }
        }

        #endregion


        private bool Login(CiiAccessLevel requestedAccess)
        {
            byte[] LoginBuffer;
            byte[] MyAddress = BackEndManager.GetLocalAddress();
            byte[] Access = BitConverter.GetBytes((uint)requestedAccess);
            int CopyLength;

#if WindowsCE
            byte[] Username = System.Text.Encoding.UTF8.GetBytes("Display");
            byte[] MachineName = System.Text.Encoding.UTF8.GetBytes("Cortex");
#else
            byte[] Username = System.Text.Encoding.UTF8.GetBytes(Environment.UserName);
            byte[] MachineName = System.Text.Encoding.UTF8.GetBytes(Environment.MachineName);
#endif

            LoginBuffer = new byte[BytesLogin.Length + Access.Length + MyAddress.Length + 64 + 64];

            Array.Copy(BytesLogin, 0, LoginBuffer, 0, BytesLogin.Length);
            Array.Copy(Access, 0, LoginBuffer, 4, Access.Length);
            Array.Copy(MyAddress, 0, LoginBuffer, 8, MyAddress.Length);

            CopyLength = Username.Length > 64 ? 64 : Username.Length;
            Array.Copy(Username, 0, LoginBuffer, 12, CopyLength);

            CopyLength = MachineName.Length > 64 ? 64 : MachineName.Length;
            Array.Copy(MachineName, 0, LoginBuffer, 76, CopyLength);

            loginAcceptEvent.Reset();

            logger.Log("LOGIN", LoginBuffer, LoginBuffer.Length);

            bool Success = BackEndManager.SendMessage(LoginBuffer);

            if (!Success)
            {
                SendAsyncError("Failed Login!");
            }
            else
            {
                Success = loginAcceptEvent.WaitOne(LoginTimeout, false);
                if (!Success)
                {
                    SendAsyncError("Login Accept timed out! " + LoginTimeout + " ms");
                }
            }
            return Success;
        }


        #region Send Actions and Gets

        private bool SendCommand(   byte[] type,
                                    uint subcommand,
                                    byte[] data,
                                    CommandCompletion completion,
                                    out uint sequenceNumber)
        {
            if (connectionState != ConnectionState.Connected)
            {
                Debug.WriteLine("Failing SendCommand() - not connected!");
                sequenceNumber = 0;
                return false;
            }

            uint newSequenceNumber = messagesInFlight.SequenceNumber;
            byte[] sequenceBytes = BitConverter.GetBytes(newSequenceNumber);
            byte[] subcommandBytes = BitConverter.GetBytes(subcommand);
            byte[] SendBuffer;

            sequenceNumber = newSequenceNumber;

            //
            //  Can't optimize this, we need a single buffer to send.
            //  We have to do the memcpy.
            //
            if (data != null)
            {
                SendBuffer = new byte[type.Length +
                                        sequenceBytes.Length +
                                        subcommandBytes.Length +
                                        data.Length];
            }
            else
            {
                SendBuffer = new byte[type.Length +
                                        sequenceBytes.Length +
                                        subcommandBytes.Length];
            }

            int destIndex = 0;

            Array.Copy(type, 0, SendBuffer, destIndex, type.Length);
            destIndex += type.Length;

            Array.Copy(sequenceBytes, 0, SendBuffer, destIndex, sequenceBytes.Length);
            destIndex += sequenceBytes.Length;

            Array.Copy(subcommandBytes, 0, SendBuffer, destIndex, subcommandBytes.Length);

            if (data != null)
            {
                destIndex += subcommandBytes.Length;
                Array.Copy(data, 0, SendBuffer, destIndex, data.Length);
            }

            logger.Log("COMMAND", SendBuffer, SendBuffer.Length);

            messagesInFlight.Add(newSequenceNumber, completion);

            bool Success = backEndManager.SendMessage(SendBuffer);

            if (!Success)
            {
                messagesInFlight.Delete(newSequenceNumber);
                sequenceNumber = 0;
            }

            return Success;
        }


        public bool SendActionCommand(  uint subcommand,
                                        byte[] data,
                                        CommandCompletion completion,
                                        out uint sequenceNumber)
        {
            if ((grantedAccess == CiiAccessLevel.AlEngineering) ||
                (grantedAccess == CiiAccessLevel.AlMaster) ||
                (grantedAccess == CiiAccessLevel.AlLocalUI))
            {
                return SendCommand(MessageTypeAction, subcommand, data, completion, out sequenceNumber);
            }
            else
            {
                sequenceNumber = 0;
                return false;
            }
        }


        public bool SendActionCommand(uint subcommand,
                                        byte[] data,
                                        CommandCompletion completion)
        {
            uint sequenceNumber;
            return SendActionCommand(subcommand, data, completion, out sequenceNumber);
        }


        public bool SendGetCommand(uint subcommand,
                                        byte[] data,
                                        CommandCompletion completion,
                                        out uint sequenceNumber)
        {
            return SendCommand(MessageTypeGet, subcommand, data, completion, out sequenceNumber);
        }

        public bool SendGetCommand(uint subcommand,
                                        byte[] data,
                                        CommandCompletion completion)
        {
            uint sequenceNumber;
            return SendGetCommand(subcommand, data, completion, out sequenceNumber);
        }

        #endregion


        public void DeleteCommandInProgress(uint sequenceNumber)
        {
            messagesInFlight.Delete(sequenceNumber);
        }
    }

}

