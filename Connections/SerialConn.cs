using Connections.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.IO.Ports;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using System.Diagnostics.Eventing.Reader;
//using System.Windows.Media;

namespace Connections
{
    public class SerialConn : AbstractConnection, ISerialConnection
    {
        private readonly SerialPort port = new SerialPort();

        static SerialConn()
        {
            ConnectionTypes.Add(typeof(SerialConn));
        }
        public SerialConn(ILogger? logger) : base(logger)  
        {
            port.BaudRate = 125000;
            //port.ReadTimeout = -1;
            //port.ReadBufferSize = 0x8000;
            //port.DataReceived += DataReceivedHandler;
            var netThread = new Thread(NetReadThread);
            netThread.Name = "====netThread====";
            netThread.IsBackground = true;
            netThread.Start();
        }
        public override string? ToString()
        {
            return PortName;
        }
        protected override void Dispose(bool disposing)
        {
            port.Dispose();
            base.Dispose(disposing);
        }
        public int BaudRate
        {
            get { return port.BaudRate; }
            set { port.BaudRate = value; }
        }
        public string PortName
        {
            get { return port.PortName; }
            set { port.PortName = value; }
        }

        public override bool IsOpen => port.IsOpen;

        public override Task Close(int timout = 2000)
        {
            logger?.LogInformation($"{Thread.CurrentThread.ManagedThreadId} {dbg} CLOSE {port.PortName}   {port.BaudRate}");
            port.Close();
            return Task.CompletedTask;
        }

        public override Task Open(int timout = 500)
        {
            logger?.LogInformation($"{Thread.CurrentThread.ManagedThreadId} {dbg} OPEN {port.PortName}   {port.BaudRate}");
            port.Open();
            return base.Open(timout);
        }
        protected readonly AutoResetEvent rxEndEvent = new AutoResetEvent(false);
        protected readonly AutoResetEvent rxDisposeEvent = new AutoResetEvent(false);
       // private bool _portDisposed = false;
        //protected override void RecreateCts()
        //{
        //    lock (_lock)
        //    {
        //        base.RecreateCts();
        //    }
        //    Cts.Token.Register(() =>
        //    {
        //        lock (_lock)
        //        {
        //            App.logger?.LogInformation($"{Thread.CurrentThread.ManagedThreadId} {dbg} Cancel Event port.Dispose() {port.PortName}   {port.BaudRate}");
        //            port.Dispose();// Close();
        //            Open();
        //            _disposed = true;
        //        }
        //    });
        //}
        protected override void BeginRead()
        {
            base.BeginRead();
            if (Cts != null)
            {
                rxDisposeEvent.Reset();
                Cts.Token.Register(() =>
                {
                    logger?.LogInformation($"{Thread.CurrentThread.ManagedThreadId} {dbg} Cancel Event port.Dispose() {port.PortName}   {port.BaudRate}");
                    port.Dispose();// Close();
                    
                    rxDisposeEvent.Set();
                });
            }
            else rxDisposeEvent.Set();
        }
        protected override void EndRead()
        {
            // WaitHandle.WaitAll(new[] { rxDisposeEvent, rxEndEvent });
            // if (!IsOpen) Open();

            if (IsReading)
            {       
                logger?.LogInformation($"{Thread.CurrentThread.ManagedThreadId} {dbg}  EndRead(BEGIN): IsReading =True");

                if (Cts != null)
                {
                    if (!Cts.IsCancellationRequested)
                    {
                        Cts.Cancel();
                        logger?.LogInformation($"{Thread.CurrentThread.ManagedThreadId} {dbg} EndRead(CONTINUE): Cts.Cancel()");
                    }
                    WaitHandle.WaitAll(new[] { rxDisposeEvent, rxEndEvent });

                    Open();
                }
                IsReading = false;
                logger?.LogInformation($"{Thread.CurrentThread.ManagedThreadId} {dbg}  EndRead(END) Wait ALL");
            }
            else
            {
                if (Cts != null && Cts.IsCancellationRequested)
                {
                    rxDisposeEvent.WaitOne();
                    Open();
                    logger?.LogInformation($"{Thread.CurrentThread.ManagedThreadId} {dbg} EndRead(END) Wait rxDisposeEvent Only ");
                }
                else
                    logger?.LogInformation($"{Thread.CurrentThread.ManagedThreadId} {dbg} EndRead(END) EasyEnd ");
            }
            base.EndRead();
        }

        protected override Task Send(DataReq dataReq)
        {
            return Task.Run(async () =>
            {
                if (!IsOpen) 
                {
                    logger?.LogInformation($"{Thread.CurrentThread.ManagedThreadId} {dbg} =====================");
                }
                await port.BaseStream.FlushAsync();
                await port.BaseStream.WriteAsync(dataReq.txBuf, 0, dataReq.txBuf.Length);
            });
        }

        private void DataReceivedHandler(object sender, SerialDataReceivedEventArgs e)
        {
            int cnt = port.BytesToRead;

            logger?.LogTrace("I:{cnt}", cnt);

            if ((cnt == 0) || IsCanceled) return;
            if (cnt + offset > rxBuf.Length) 
            { 
                logger?.LogError("Buffer OverFlow rxBuf.Length {rxBufLength } < {cntoff} {off} {cnt} ", 
                    rxBuf.Length, cnt + offset, offset, cnt);
                Array.Resize(ref rxBuf, offset+cnt);
            }
            int incnt = port.Read(rxBuf, offset, cnt);
            if (incnt == 0) return;
            lock (_lock)
            {
                oldOffset = offset;
                offset += incnt;
            }
            rxRowEvent.Set();
        }
        private async void NetReadThread()
        {
            while (true)
            {
                try
                {
                    if (disposed) return;
                    int cntin = currenRq != null ? currenRq.rxCount : rxBuf.Length;

                    if (!port.IsOpen || !IsReading || cntin <= 0 || Cts == null)
                    {
                        rxEndEvent.Set();
                        continue;
                    }
                    rxEndEvent.Reset();
                        try
                        {
                            int cntout = 0;
                            while (port.IsOpen && cntout == 0 && !Cts.IsCancellationRequested)
                            {
                               // if (offset > 200) App.logger?.LogInformation("I");
                                cntout = await port.BaseStream.ReadAsync(rxBuf, offset, cntin - offset, Cts.Token);
                            if (cntout == 0) logger?.LogInformation("Z");
                            if (cntout > 0 && cntout < 3 )
                                {
                                    Thread.Sleep(0);
                                    cntout += await port.BaseStream.ReadAsync(rxBuf, offset + cntout, cntin - offset - cntout, Cts.Token);
                                }
                            //if (offset > 200) App.logger?.LogInformation("E");
                            }
                            if (Cts.IsCancellationRequested)
                            {
                                IsReading = false;
                                rxEndEvent.Set();
                                continue;
                            }
                            if (cntout + offset > rxBuf.Length) { throw new Exception("Buffer OverFlow"); }

                            lock (_lock)
                            {
                                oldOffset = offset;
                                offset += cntout;

                                if (offset >= cntin)
                                {
                                    IsReading = false;
                                }
                                rxRowEvent.Set();
                            }

                           // logger?.LogTrace("I:{cnt} {off}", cntout, offset);

                        }
                        catch (Exception e) 
                        {
                            IsReading = false;
                            logger?.LogInformation($"ERR {Thread.CurrentThread.ManagedThreadId} {dbg} NetReadThread exit rxEndEvent.Set() {e}");
                            rxEndEvent.Set();
                        }
                }
                catch (Exception e)
                {
                    logger?.LogError(e.Message,e);
                }
            }

        }

    }
}
