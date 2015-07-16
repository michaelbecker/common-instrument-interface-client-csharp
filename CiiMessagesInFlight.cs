using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;


namespace TAInstruments.CommonInstrumentInterface
{

    internal class CiiMessageTracker
    {
        public CiiMessageTracker(CommandCompletion completion)
        {
            ackReceived = false;
            this.completion = completion;
        }

        private CommandCompletion completion;
        public CommandCompletion Completion
        {
            get
            {
                return completion;
            }
        }

        private volatile bool ackReceived;
        public bool AckReceived
        {
            get
            {
                return ackReceived;
            }
            set
            {
                if (value == false)
                {
                    throw new InvalidOperationException("Cannot reset ACK state!");
                }
                ackReceived = value;
            }
        }
    }


    internal class CiiMessagesInFlight
    {
        private uint sequenceNumberGenerator;
        private object sequenceNumberLock;
        private Dictionary<uint, CiiMessageTracker> messagesInFlight;

        public CiiMessagesInFlight()
        {
            sequenceNumberLock = new object();
            sequenceNumberGenerator = 0xFFFFFF00;
            messagesInFlight = new Dictionary<uint, CiiMessageTracker>();
        }

        public uint SequenceNumber
        {
            get
            {
                lock (sequenceNumberLock)
                {
                    do
                    {
                        sequenceNumberGenerator++;
                        if (sequenceNumberGenerator == 0)
                        {
                            sequenceNumberGenerator++;
                        }
                    }
                    while (messagesInFlight.ContainsKey(sequenceNumberGenerator));

                    return sequenceNumberGenerator;
                }
            }
        }

        public void Add(uint sequenceNumber, CommandCompletion completion)
        {
            lock (sequenceNumberLock)
            {
                if (messagesInFlight.ContainsKey(sequenceNumber))
                {
                    throw new InvalidOperationException("Internal Error - duplicate messages in flight");
                }

                //Debug.WriteLine("Adding sequenceNumber - " + sequenceNumber);
                messagesInFlight.Add(sequenceNumber, new CiiMessageTracker(completion));
            }
        }

        public void Delete(uint sequenceNumber)
        {
            lock (sequenceNumberLock)
            {
                //Debug.WriteLine("Removing sequenceNumber - " + sequenceNumber);
                if (sequenceNumber != 0)
                {
                    messagesInFlight.Remove(sequenceNumber);
                }
            }
        }

        public CiiMessageTracker Retrieve(uint sequenceNumber)
        {
            lock (sequenceNumberLock)
            {
                CiiMessageTracker messageTracker;
                bool Success = messagesInFlight.TryGetValue(sequenceNumber, out messageTracker);
                if (Success)
                {
                    return messageTracker;
                }
                else
                {
                    return null;
                }
            }
        }

        public void Clear()
        {
            lock (sequenceNumberLock)
            {
                messagesInFlight.Clear();
            }
        }
    }

}

