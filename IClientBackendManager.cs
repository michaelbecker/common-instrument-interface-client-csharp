using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TAInstruments.CommonInstrumentInterface
{
    public interface IClientBackEndManager
    {
        bool Connect();
        void Disconnect();

        bool SendMessage(byte[] buffer);
        byte[] GetLocalAddress();

        ///<remarks>
        ///  Disconnects on the socket mgr will often be async, 
        ///  but connects should always be sync, at least at this 
        ///  layer.  This will not be invoked if you called Disconnect 
        ///  yourself.
        ///</remarks>
        event DisconnectEventHandler AsyncDisconnectEvent;
    }
}

