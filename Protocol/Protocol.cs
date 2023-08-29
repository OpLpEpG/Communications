using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Animation;
using CRCModbusRTU;
using Connections.Interface;
using System.Threading;
using System.Drawing;
using ExceptionExtensions;
using System.Windows.Interop;
using Connections;
using Microsoft.Extensions.Logging;

/// реализация стандартных команд
namespace Commands
{
    using Communications.Properties;
    public enum Cmds
    {
        // команды адрес != 0xF
        CMD_READ_RAM = 1,
        CMD_INFO = 2,
        CMD_READ_EE = 5,
        CMD_WRITE_EE = 6,
        CMD_WORK = 7,
        CMD_BOOT = 8,
        // широковещательные команды адрес 0xF
        CMD_SYNC = 0x5, // Delay, Off
        CMD_RESYNC = 0xA,
        CMD_TURBO = 0xD, // для последовательного порта
        // bootloader command
        CMD_BOOT_TEST = 8,
        CMD_BOOT_READ = 0xD,
        CMD_BOOT_EXIT = 0xE,
        CMD_BOOT_WRITE = 0xF,
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WorkDataHeader
    {
        public byte status;
        public int kadr;
    }
    class MetaDataException : FlagsException
    {
        public MetaDataException(string? mesg = null, bool Dialog = false, bool LogFile = false, bool LogWindow = true, bool LogStack = false)
            : base(mesg, Dialog, LogFile, LogWindow, LogStack)
        { }
    };
    class ReadRamException : FlagsException
    {
        public ReadRamException(string? mesg = null, bool Dialog = false, bool LogFile = false, bool LogWindow = true, bool LogStack = false)
            : base(mesg, Dialog, LogFile, LogWindow, LogStack)
        { }
    };
    class EEPROMException : FlagsException
    {
        public EEPROMException(string? mesg = null, bool Dialog = true, bool LogFile = false, bool LogWindow = true, bool LogStack = false)
            : base(mesg, Dialog, LogFile, LogWindow, LogStack)
        { }
    };
    class WorkException : FlagsException
    {
        public WorkException(string? mesg = null, bool Dialog = false, bool LogFile = false, bool LogWindow = true, bool LogStack = false)
            : base(mesg, Dialog, LogFile, LogWindow, LogStack)
        { }
    };
    public class Protocol
    {
        public readonly int MaxTxDataLen;
        private readonly ushort adr;
        private readonly byte cmd;
        private readonly int dataLen;
        private int dataPtr;

        #region Protocol
        private Protocol(ushort adr, Cmds cmd, int dataLen)
        {
            this.adr = adr;
            this.cmd = (byte)cmd;
            if (adr == 0xFF) { this.cmd |= 0xF0; }; // совместимось с однобайтным режимом
            this.dataLen = dataLen;
            MaxTxDataLen = 255 - 2 - SizeOfAC;
        }
        public unsafe static (ushort, byte) GetAC(byte[] buf, int SizeAC)
        {
            ushort adr = 0;
            byte cmd = 0;
            switch (SizeAC)
            {
                case 1:
                    adr = (ushort)((buf[0] & 0xF0) >> 4);
                    cmd = (byte)(buf[0] & 0x0F);
                    break;
                case 2:
                    adr = buf[0];
                    cmd = buf[1];
                    break;
                case 3:
                    fixed (byte* ptr = &buf[1]) adr = *(ushort*)ptr;
                    cmd = buf[0];
                    break;
            }
            return (adr, cmd);
        }
        public int SizeOf { get { return SizeOfAC + dataLen + 2; } }
        public int SizeOfAC { get { return adr switch { > 255 => 3, > 15 => 2, _ => 1 }; } }
        public static int GetSizeOfAC(int adr) => adr switch { > 255 => 3, > 15 => 2, _ => 1 };         

        public unsafe void Begin(byte[] buf)
        {
            if (adr > 255) 
            { 
               fixed(byte* ptr = &buf[1]) *(ushort*)ptr = adr;
               buf[0] = cmd; 
            }
            else if (adr > 15) { buf[0] = (byte) adr; buf[1] = cmd; }
            else { buf[0] = (byte)( (adr << 4) | cmd); }
            dataPtr = SizeOfAC;
        }
        public void NextArray(byte[] buf, byte[] data)
        {
            Array.Copy(data,0, buf, dataPtr, data.Length);
            dataPtr += data.Length;
        }
        public unsafe void Next<T>(byte[] buf, T t) where T : unmanaged
        {
            fixed(byte* ptr = &buf[dataPtr]) 
            { 
                T* ptr2 = (T*)ptr;
                *ptr2 = t;
            }
            dataPtr += sizeof(T);

            if(buf.Length < dataPtr) 
            {
                throw new FlagsException("buf.Length < dataPtr");
            }
        }
        public void End(byte[] buf)
        {
            Next<ushort>(buf, Crc.ComputeCrc(0xFFFF, buf, 0, dataPtr));
            if(buf.Length != dataPtr) { throw new FlagsException("buf.Length != dataPtr"); }
        }
        public bool Check(DataResp? resp)
        {
            if (resp?.rxCount == resp?.Req.rxCount)
            {
                for (int i = 0; i < SizeOfAC; i++)
                {
                    if (resp?.rxBuf[i] != resp?.Req.txBuf[i]) return false;
                }
                return true;
            }
            else return false;
        }

        public static bool Check(DataResp? resp, int checkCount = 1) 
        {
            if(resp?.rxCount == resp?.Req.rxCount)
            {
                for (int i = 0; i < checkCount; i++)
                {
                    if (resp?.rxBuf[i] != resp?.Req.txBuf[i]) return false;
                }
                return true; 
            }else return false;
        }
        #endregion

        public static string CreateCheckExceptionMessage(string strType, DataResp r, Protocol p)
        {
            string adr = "";
            string cmd = "";
            string cnt;

            string err = Resources.strError;
            if (r.rxCount == -1) cnt = Resources.strTimeout;
            else if (r.rxCount == -2) cnt = Resources.strCancel;
            else
            {
                cnt = $" Req {r.Req.rxCount} Resp {r.rxCount}";
                var (a, c) = GetAC(r.rxBuf, p.SizeOfAC);
                adr = " " + a.ToString();
                cmd = " " + c.ToString();
            }
            return $"[{p.adr}{adr}:{p.cmd}{cmd}] {err}: {strType} : {cnt}";
        }

        #region MetaData

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct MetaDataHeader
        {
            public byte RecType;
            public ushort len;
        }

        unsafe int GetMetaDataLen(DataResp resp)
        {            
            fixed (byte* b = &resp.rxBuf[SizeOfAC])
            {
                MetaDataHeader* h= (MetaDataHeader*)b;
                if (h->RecType == 0x24) return h->len; 
                else return *(ushort*)b;                         
            }
        }

        public static (DataReq, Protocol) MetaData(ushort adr, ushort from, byte count, bool NeedFrom = false,  OnResponseEvent? resp = null, int timout = 500)
        {
            var p = new Protocol(adr, Cmds.CMD_INFO, ((from > 0) || NeedFrom) ? 2 + 1 : 1);
            if (count > p.MaxTxDataLen) 
            {
                string DataLen = Resources.strDataLen;
                string ErrMetadata = Resources.strErrMetadata;
                string MaxDataLen = Resources.strMaxDataLen;
                throw new FlagsArgumentOutOfRangeException(DataLen, count,  $"{ErrMetadata}. {MaxDataLen}: {p.MaxTxDataLen}"); 
            }   
            var b = new byte[p.SizeOf];
            p.Begin(b);
            p.Next<byte>(b, count);
            if ((from> 0) || NeedFrom) p.Next<ushort>(b, from);
            p.End(b);
            var r = new DataReq(b, count + p.SizeOfAC + 2, resp, timout);
            return (r, p);
        }

        public static async Task<List<byte>> ReadMetaData(IConnection con, ushort adr, OnResponseEvent? resp = null, int timout = 500)
        {
            string readMetaData = Resources.strReadMetaData;
            var (rq, p) = MetaData(adr, 0, 3, false, resp, timout);
            var r = await con.SendAsync(rq);
            var md = new List<byte>();
            if (!p.Check(r)) throw new MetaDataException(CreateCheckExceptionMessage(readMetaData, r,p));            
            var len = p.GetMetaDataLen(r);
            ushort from = 0;
            while (len > 0)
            {
                byte l = (byte)(len > p.MaxTxDataLen ? p.MaxTxDataLen : len);
                (rq, p) = MetaData(adr, from, l, true, resp, timout);
                r = await con.SendAsync(rq);
                if (r.rxCount < 0) throw new MetaDataException(CreateCheckExceptionMessage(readMetaData, r, p));
                ushort readed = (ushort)(r.rxCount - p.SizeOfAC - 2);
                len -= readed;
                from += readed;
                md.AddRange(r.rxBuf.Skip(p.SizeOfAC).Take(readed));
            }
            return md;
        }
        #endregion

        #region Ram
        public static (DataReq, Protocol) Ram( ushort adr, uint from, uint count, OnResponseEvent? resp = null, int timout = 500) 
        {
            var p = new Protocol( adr, Cmds.CMD_READ_RAM, sizeof(uint)*2);
            var b = new byte[p.SizeOf];
            p.Begin(b);
            p.Next<uint>(b, from);
            p.Next<uint>(b, count);
            p.End(b);
            var r = new DataReq(b, (int)count + p.SizeOfAC + 2, resp, timout);
            return (r, p); 
        }
        public static async Task<(DataResp, Protocol)> ReadRam(IConnection con, ushort adr, uint from, uint count, 
            int timout = 2000, int MaxBad=20, ILogger? logger = null)
        {
            var (rq, p) = Ram(adr, from, count, null, timout);
            DataResp r;
            int i = 0;
            do
            {
                r = await con.SendAsync(rq);
                if (con.IsCanceled)
                {
                    string readRamCancel = Resources.strReadRamCancel;
                    throw new ReadRamException(String.Format(readRamCancel, con), Dialog: true);
                }
                if (!p.Check(r))
                {
                    await con.Close();
                    logger?.LogError("ReadRam ERR A:{adr} F:{from} B:{bad} ",adr, from, i);
                    await con.Open();
                    continue;
                }
                return (r, p);

            } while(i++< MaxBad);
            string readRam = Resources.strReadRam;
            throw new ReadRamException(CreateCheckExceptionMessage(readRam, r, p));
        }
        #endregion

        #region EEPROM
        public static (DataReq, Protocol) Eeprom_Read(ushort adr, ushort from, byte count, OnResponseEvent? resp = null, int timout = 500)
        {
            var p = new Protocol(adr, Cmds.CMD_READ_EE, sizeof(ushort) + sizeof(byte));
            var b = new byte[p.SizeOf];
            p.Begin(b);
            p.Next<ushort>(b, from);
            p.Next<byte>(b, count);
            p.End(b);
            int rxCnt = count + p.SizeOfAC + 2;
            if (rxCnt > 255) 
            {
                string param = Resources.strPacketLen;
                string err = Resources.errEEPread;
                throw new FlagsArgumentOutOfRangeException(param, p.SizeOf, err);
            }
            var r = new DataReq(b, rxCnt, resp, timout);
            return (r, p);
        }
        public static async Task<byte[]> ReadEep(IConnection con, ushort adr, ushort from, ushort count)
        {
            var (rq, p) = Eeprom_Read(adr, 0, 1);
            var md = new List<byte>();
            while (count > 0)
            {
                byte l = (byte)(count > p.MaxTxDataLen ? p.MaxTxDataLen : count);
                (rq, p) = Eeprom_Read(adr, from, l);
                var r = await con.SendAsync(rq);
                if (!p.Check(r)) throw new EEPROMException(CreateCheckExceptionMessage(Resources.strEEPRead, r, p));
                ushort readed = (ushort)(r.rxCount - p.SizeOfAC - 2);
                count -= readed;
                from += readed;
                md.AddRange(r.rxBuf.Skip(p.SizeOfAC).Take(readed));
            }
            return md.ToArray();
        }

        public static (DataReq, Protocol) Eeprom_Write(ushort adr, ushort from, byte[] data, OnResponseEvent? resp = null, int timout = 2000)
        {
            var p = new Protocol(adr, Cmds.CMD_WRITE_EE, sizeof(ushort) + data.Length);
            if (p.SizeOf > 255)
            {
                string param = Resources.strPacketLen;
                string err = Resources.errEEPWrite;
                throw new FlagsArgumentOutOfRangeException(param, p.SizeOf,err);
            }
            var b = new byte[p.SizeOf];
            p.Begin(b);
            p.Next<ushort>(b, from);
            p.NextArray(b, data);
            p.End(b);
            int rxCnt = p.SizeOfAC + 2;
            var r = new DataReq(b, rxCnt, resp, timout);
            return (r, p);
        }
        public static async Task WriteEep(IConnection con, ushort adr, ushort from, byte[] data)
        {
            var (rq, p) = Eeprom_Read(adr, 0, 1);
            var maxDataLen = p.MaxTxDataLen - sizeof(ushort);
            int count = data.Length;
            int index = 0;
            int bad = 0; 
            while (count > 0)
            {
                byte l = (byte)(count > maxDataLen ? maxDataLen : count);
                var a = new byte[l];
                Array.Copy(data, index, a, 0, l);
                (rq, p) = Eeprom_Write(adr, from, a);
                var r = await con.SendAsync(rq);
                if (!p.Check(r))
                {
                    if (bad++ < 5) continue;   
                    throw new EEPROMException(CreateCheckExceptionMessage(Resources.strEEPWrite, r, p));
                }
                count -= l;
                from += l;
                index += l;
                bad = 0;
            }
        }
        #endregion

        #region режим информации

        public static (DataReq, Protocol) Work(ushort adr, ushort cnt, OnResponseEvent? resp = null, int timout = 2000)
        {
            var p = new Protocol(adr, Cmds.CMD_WORK, (cnt <= 255) ? sizeof(byte): sizeof(ushort));
            var b = new byte[p.SizeOf];
            p.Begin(b);
            if (cnt <= 255) p.Next<byte>(b, (byte)cnt); else p.Next<ushort>(b, cnt);
            p.End(b);
            int rxCnt = cnt + p.SizeOfAC + 2;
            var r = new DataReq(b, rxCnt, resp, timout);
            return (r, p);
        }
        public static async Task<(DataResp, Protocol)> ReadWork(IConnection con, byte adr, ushort cnt)
        {
            var (rq, p) = Work(adr, cnt);
            var r = await con.SendAsync(rq);
            if (!p.Check(r)) throw new WorkException(CreateCheckExceptionMessage("чтения информации", r, p));
            return (r, p);
        }
        #endregion

        #region Delay Command
        public static (DataReq, Protocol) Delay(int kadr, bool Bt2 = false, OnResponseEvent? resp = null, int timout = 100)
        {
            var p = new Protocol((byte) (Bt2 ? 0xFF : 0xF) , Cmds.CMD_SYNC, sizeof(int));
            var b = new byte[p.SizeOf];
            p.Begin(b);
            p.Next<int>(b, kadr);
            p.End(b);
            int rxCnt = -1;
            var r = new DataReq(b, rxCnt, resp, timout);
            return (r, p);
        }
        public static async Task<(DataResp, Protocol)> SetDelay(IConnection con,int kadr, bool Bt2 = false)
        {
            var (rq, p) = Delay(kadr, Bt2);
            var r = await con.SendAsync(rq);
            return (r, p);
        }
        #endregion

        #region Turbo Command
        public static (DataReq, Protocol) Turbo(byte speed, bool Bt2 = false, OnResponseEvent? resp = null, int timout = 100)
        {
            var p = new Protocol((byte)(Bt2 ? 0xFF : 0xF), Cmds.CMD_TURBO, sizeof(byte));
            var b = new byte[p.SizeOf];
            p.Begin(b);
            p.Next<byte>(b, speed);
            p.End(b);
            int rxCnt = -1;
            var r = new DataReq(b, rxCnt, resp, timout);
            return (r, p);
        }

        public static async Task<(DataResp, Protocol)> SetTurbo(IConnection con, byte speed, bool Bt2 = false)
        {
            var (rq, p) = Turbo(speed, Bt2);
            var r = await con.SendAsync(rq);
            return (r, p);
        }
        #endregion
    }
}
