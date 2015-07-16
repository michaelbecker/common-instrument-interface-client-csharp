using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.IO;


namespace TAInstruments.CommonInstrumentInterface
{
    internal class Logger
    {
        #region Private members

        private readonly string touchLogFileName;
        private readonly string logPath;
        private Thread loggerThread;
        private object logLock;
        private Queue<LogMessage> messageQueue;
        private volatile bool loggingEnabled;
        private AutoResetEvent messageAvailable;

        #endregion


        private class LogMessage
        {
            private DateTime timestamp;
            private byte[] data;
            private string message;

            public override string ToString()
            {
                //
                //  TODO -  is it better to alloc a new one all the time?
                //          or reuse one and clear it out?
                //
                StringBuilder s = new StringBuilder(timestamp.ToString() + " ", 5000);
                if (message != null)
                {
                    s.Append(message);
                    s.Append(" ");
                }
                if (data != null)
                {
                    s.Append(BitConverter.ToString(data));
                    s.Replace("-", "");
                }
                return s.ToString();
            }


            public LogMessage(string message, byte [] buffer, int dataLength)
            {
                timestamp = DateTime.Now;
                if (message != null)
                {
                    this.message = message;
                }
                if (buffer != null)
                {
                    data = new byte[dataLength];
                    Array.Copy(buffer, data, dataLength);
                }
            }
        }

        
        private void LoggerThread()
        {
            bool wasEnabled = false;
            StreamWriter writer = null;
            Random random = new Random();

            while (true)
            {
                //
                //  We poll every second to support turning logging off or on.
                //
                bool isMessageAvailable = messageAvailable.WaitOne(1000, false);

                //
                //  Check if we need to start logging here.
                //
                loggingEnabled = File.Exists(touchLogFileName);

                //
                //  Turning it on, create a file.
                //
                if (loggingEnabled && !wasEnabled)
                {
                    DateTime now = DateTime.Now;
                    int r = random.Next();

                    string filename =   logPath + "\\" + 
                                        now.Year + "_" +
                                        now.Month + "_" +
                                        now.Day + "_" +
                                        now.Hour + "__" +
                                        now.Minute + "_" +
                                        now.Second +  "_" + 
                                        r + ".log";
                    writer = File.CreateText(filename);
                }
                //
                //  Turning it off, close the file.
                //
                else if (!loggingEnabled && wasEnabled)
                {
                    writer.Flush();
                    writer.Close();
                }

                wasEnabled = loggingEnabled;


                if (!isMessageAvailable || !loggingEnabled)
                {
                    continue;
                }

                //
                //  LOCK --------------------------------------------
                //
                Monitor.Enter(logLock);

                while (messageQueue.Count != 0)
                {
                    LogMessage msg = messageQueue.Dequeue();

                    //
                    //  UNLOCK --------------------------------------
                    //
                    Monitor.Exit(logLock);


                    //Debug.WriteLine(msg.ToString());
                    writer.WriteLine(msg.ToString());

                    //
                    //  LOCK ----------------------------------------
                    //
                    Monitor.Enter(logLock);
                }

                //
                //  UNLOCK ------------------------------------------
                //
                Monitor.Exit(logLock);
            }
        }


        public void Log(string message, byte [] buffer, int dataLength)
        {
            if (loggingEnabled)
            {
                LogMessage msg = new LogMessage(message, buffer, dataLength);

                lock (logLock)
                {
                    messageQueue.Enqueue(msg);
                    messageAvailable.Set();
                }
            }
        }


        private Logger()
        {
            if (Environment.OSVersion.Platform == PlatformID.WinCE)
            {
                logPath = @"\Temp";
                touchLogFileName = @"\Temp\CIILOG";
            }
            else
            {
                logPath = @"C:\Temp";
                touchLogFileName = @"C:\Temp\CIILOG";
            }
            logLock = new object();
            loggingEnabled = false;
            messageAvailable = new AutoResetEvent(false);
            messageQueue = new Queue<LogMessage>(500);

            loggingEnabled = File.Exists(touchLogFileName);

            loggerThread = new Thread(LoggerThread);
            loggerThread.IsBackground = true;
            loggerThread.Name = "CII Logger";
            loggerThread.Priority = ThreadPriority.AboveNormal;
            loggerThread.Start();
        }


        #region Singleton Code

        private static readonly Logger instance = new Logger();
        /// <summary>
        /// This is a singleton and is created in a static constructor.
        /// </summary>
        public static Logger Instance
        {
            get
            {
                return instance;
            }
        }

        #endregion
    }
}
