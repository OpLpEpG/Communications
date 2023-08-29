using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;


namespace Connections.Interface
{
    public interface IAbstractConnection : IDisposable
    {
        Task Open(int timout = 10000);
        Task Close(int timout = 5000);
        bool IsOpen { get; }

        void Cancel();
        bool IsCanceled { get; }
        CancellationTokenSource? CancelTokenSource { get; }

        // для захвата соединения
        bool Lock();
        void UnLock();
        bool IsLocked { get; }
    }
    public interface ISSDConnection : IAbstractConnection
    {
        char Letter { get; set; }
        long SSDSize { get; }
        uint SectorSize { get; }
        SafeFileHandle? handle { get; }
    }

    public delegate void OnResponseEvent(object Sender, DataResp arg);

    public record DataReq(byte[] txBuf, int rxCount, OnResponseEvent? OnResponse = null, int timout = 500)
    {
        public DataReq(DataReq r, OnResponseEvent ev, int timout = 500) : this(r.txBuf, r.rxCount, ev, timout) { }
    }

    public record DataResp(DataReq Req, byte[] rxBuf, int rxCount)
    {
        public DataResp(DataReq Req, int rxCount) : this(Req, new byte[RxCount(rxCount)], rxCount) { }

        public static int RxCount(int cnt)
        {
            return cnt < IConnection.MIN_RX_BUF ? IConnection.MIN_RX_BUF : cnt;
        }
    }
    public interface IConnection : IAbstractConnection
    {
        const int MIN_RX_BUF = 4096;
        Task<DataResp> SendAsync(DataReq dataReq);
    }
    public interface INetConnection : IConnection
    {
        int Port { get; set; }
        string Host { get; set; }
    }
    public interface ISerialConnection : IConnection
    {
        int BaudRate { get; set; }
        string PortName { get; set; }
    }
}
