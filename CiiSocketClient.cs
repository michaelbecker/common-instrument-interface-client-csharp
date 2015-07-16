using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;



namespace TAInstruments.CommonInstrumentInterface
{
    /// <summary>
    /// The real class that implements TCP socket communications 
    /// with a Mercury instrument.
    /// </summary>
    public class CiiSocketClient : CiiClient
    {
        SocketClientBackEndManager socketClientBackEndManager;

        public CiiSocketClient(string serverAddress)
        {
            //
            //  Create the appropriate backend and send it to 
            //  the generic CiiClient handler.
            //
            socketClientBackEndManager = new SocketClientBackEndManager(serverAddress, this);
            BackEndManager = socketClientBackEndManager;
        }

        public int SendTimeout
        {
            get
            {
                return socketClientBackEndManager.SendTimeout;
            }
            set
            {
                socketClientBackEndManager.SendTimeout = value;
            }
        }

        public int ReceiveTimeout
        {
            get
            {
                return socketClientBackEndManager.ReceiveTimeout;
            }
            set
            {
                socketClientBackEndManager.ReceiveTimeout = value;
            }
        }

    }
}

