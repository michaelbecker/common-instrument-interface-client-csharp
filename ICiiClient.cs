using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TAInstruments.CommonInstrumentInterface
{
    /// <summary>
    /// Mercury Trios comm specified Access Levels.
    /// </summary>
    public enum CiiAccessLevel
    {
        AlInvalid = 0,
        AlViewOnly = 1,
        AlMaster = 2,
        AlLocalUI = 3,
        AlEngineering = 1000
    }


    /// <summary>
    /// Mercury Trios comm specified message statuses.
    /// </summary>
    public enum CiiMsgStatus
    {
        MsSuccess = 0,              /**  It worked. */
        MsFailed = 1,               /**  It didn't work. */
        MsUnknownCommand = 2,       /**  Unknown command message. */
        MsMalformedMessage = 3,     /**  Trouble parsing the message at the protocol level */
        MsBusy = 4,                 /**  Try again later... */
        MsNotLoggedIn = 5,          /**  Try again later... */
        MsAccessDenied = 6,         /**  Try again later... */
        MsOperationTimedOut = 7,    /**  Internal Timeout */
        MsUserSpecific = 256,       /**  Everything past this is custom */
    }


    /// <summary>
    /// The callback used for receiving status messages.
    /// </summary>
    /// <param name="substatus"></param>
    /// <param name="buffer"></param>
    /// <param name="startingOffset"></param>
    /// <param name="dataLength"></param>
    /// <remarks>
    /// We pass you the actual received buffer as an optimization.  
    /// You need to pay attention to where to start to deserialize the 
    /// buffer based on StartingOffset and DataLength.  
    /// </remarks>
    public delegate void ReceiveStatusHandler(uint substatus, byte[] buffer, int startingOffset, int dataLength);


    public delegate void ReceiveAckHandler(object userData, uint sequenceNumber);
    public delegate void ReceiveNakHandler(object userData, uint sequenceNumber, uint errorCode);
    public delegate void ReceiveResponseHandler(    object userData,
                                                    uint sequenceNumber,
                                                    uint subcommand,
                                                    uint statusCode,
                                                    byte[] data,
                                                    int startingOffset,
                                                    int dataLength);


    public class CiiAsyncErrorEventArgs : EventArgs
    {
        public string ErrorDescription {get; private set; }

        public CiiAsyncErrorEventArgs(string errorDescription)
        {
            ErrorDescription = errorDescription;
        }
    }


    /// <summary>
    /// If something goes wrong, this is called.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    /// <remarks>
    /// FYI - These callbacks are made on a different thread.
    /// </remarks>
    public delegate void AsyncErrorEventHandler(object sender, CiiAsyncErrorEventArgs e);


    /// <summary>
    /// This is called when you become connected.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    /// <remarks>
    /// These callbacks can be made on your thread or a different one.
    /// </remarks>
    public delegate void ConnectEventHandler(object sender, EventArgs e);

    /// <summary>
    /// This is called when you become disconnected.
    /// </summary>
    /// When this is called, you are out of sync with the Instrument.
    /// This will always be called first:
    /// 1) DisconnectEventHandler
    /// 2) DisconnectWarningEventHandler
    /// 3) DisconnectErrorEventHandler
    /// <param name="sender"></param>
    /// <param name="e"></param>
    /// <remarks>
    /// FYI - These callbacks are made on a different thread.
    /// </remarks>
    public delegate void DisconnectEventHandler(object sender, EventArgs e);
    
    
    /// <summary>
    /// This is called after you've been disconnected for warningDelay seconds.
    /// </summary>
    /// This will always be called second:
    /// 1) DisconnectEventHandler
    /// 2) DisconnectWarningEventHandler
    /// 3) DisconnectErrorEventHandler
    /// <param name="sender"></param>
    /// <param name="e"></param>
    /// <remarks>
    /// FYI - These callbacks are made on a different thread.
    /// </remarks>
    public delegate void DisconnectWarningEventHandler(object sender, EventArgs e);


    /// <summary>
    /// This is called after you've been disconnected for errorDelay seconds.
    /// </summary>
    /// This will always be called third:
    /// 1) DisconnectEventHandler
    /// 2) DisconnectWarningEventHandler
    /// 3) DisconnectErrorEventHandler
    /// After this is called, the comms layer aborts further communications 
    /// attempts. There will be no more retries.
    /// <param name="sender"></param>
    /// <param name="e"></param>
    /// <remarks>
    /// FYI - These callbacks are made on a different thread.
    /// </remarks>
    public delegate void DisconnectErrorEventHandler(object sender, EventArgs e);


    /// <summary>
    /// The interface to get information resulting from a SendActionCommand() or SendGetCommand().
    /// </summary>
    public class CommandCompletion
    {
        public CommandCompletion()
        {
            AckHandler = null;
            NakHandler = null;
            ResponseHandler = null;
            UserData = null;
        }

        public CommandCompletion(ReceiveAckHandler receiveAck,
                                    ReceiveNakHandler receiveNak,
                                    ReceiveResponseHandler receiveResponse)
        {
            AckHandler = receiveAck;
            NakHandler = receiveNak;
            ResponseHandler = receiveResponse;
            UserData = null;
        }

        public CommandCompletion(ReceiveAckHandler receiveAck,
                                    ReceiveNakHandler receiveNak,
                                    ReceiveResponseHandler receiveResponse,
                                    object userData)
        {
            AckHandler = receiveAck;
            NakHandler = receiveNak;
            ResponseHandler = receiveResponse;
            UserData = userData;
        }

        // OPAQUE REFERENCE TO USER DATA
        public object UserData { get; private set; }
        public ReceiveAckHandler AckHandler { get; private set; }
        public ReceiveNakHandler NakHandler { get; private set; }
        public ReceiveResponseHandler ResponseHandler { get; private set; }

}



    /// <summary>
    /// This is the Client interface to the Mercury communications API.
    /// </summary>
    /// <remarks>
    /// Application code should confine itself to this interface,
    /// after it has made the apropriate unique CiiClient.
    /// </remarks>
    public interface ICiiClient
    {
        bool Connect(CiiAccessLevel requestedAccess);
        void Disconnect();
        bool IsConnected { get; }


        /// <summary>
        /// This allows you to get a specific status message delivered to you.
        /// </summary>
        /// <param name="statusMessage"></param>
        /// <param name="statusDelegate"></param>
        /// <returns></returns>
        bool RegisterStatusHandler(uint statusMessage, ReceiveStatusHandler statusDelegate);

        /// <summary>
        /// If a message is not consumed by a registered status handler (above), 
        /// and there is an unhandled status handler, it will be called.
        /// </summary>
        /// <param name="statusDelegate"></param>
        /// <returns></returns>
        /// <remarks>
        /// There can only be one of these registered.
        /// </remarks>
        bool RegisterUnhandledStatusHandler(ReceiveStatusHandler statusDelegate);


        event AsyncErrorEventHandler AsyncErrorEvent;
        event ConnectEventHandler ConnectEvent;
        event DisconnectEventHandler DisconnectEvent;
        event DisconnectWarningEventHandler DisconnectWarning;
        event DisconnectErrorEventHandler DisconnectError;


        /// <summary>
        /// Allows you to change the defaults of when the comms library 
        /// will send you a DisconnectWarningEventHandler event and a
        /// DisconnectErrorEventHandler.
        /// </summary>
        /// When the OS tells the comms layer that the socket is 
        /// disconnected, we send the DisconnectEvent right then.
        /// When warningDelay seconds have elapsed after this point, 
        /// we send the DisconnectWarningEventHandler event. 
        /// When errorDelay seconds have from the disconnect, we send the 
        /// DisconnectErrorEventHandler event, and we stop retrying.
        /// If the math is wrong we just ignore the invalid parameter(s).
        /// The following must be true:
        ///     errorDelay > warningDelay > 0
        /// <param name="warningDelay">Delay in seconds from Disconnect to callback.
        /// Default value = 5 seconds.</param>
        /// <param name="errorDelay">Delay in seconds from Disconnect to callback
        /// Default value = 30 seconds.</param>
        void SetCommFailureTimeouts(int warningDelay, int errorDelay);

        CiiAccessLevel GrantedAccess { get; }


        #region Send Message API

        bool SendActionCommand( uint subcommand,
                                byte[] data,
                                CommandCompletion completion,
                                out uint sequenceNumber);


        bool SendActionCommand(uint subcommand, 
                                byte[] data,
                                CommandCompletion completion
                                );


        bool SendGetCommand(    uint subcommand,
                                byte[] data,
                                CommandCompletion completion,
                                out uint sequenceNumber);


        bool SendGetCommand(    uint subcommand,
                                byte[] data,
                                CommandCompletion completion
                                );
        #endregion


        /// <summary>
        /// This allows you to cancel a Get or Action command while it is being worked on.
        /// </summary>
        /// <param name="sequenceNumber"></param>
        /// <remarks>
        /// Calling this does "NOT" guarantee that your callbacks won't be called.
        /// There are implicit race conditions with this type of operation.
        /// You need to be ready to handle callbacks you cancelled even AFTER 
        /// you have called this.
        /// </remarks>
        void DeleteCommandInProgress(uint sequenceNumber);

    }
}



