using Commands;
using Connections.Interface;
using Connections;
using CRCModbusRTU;
using ExceptionExtensions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Connections
{
    using Communications.Properties;
    using System.IO.Pipes;

    using System.Runtime.Intrinsics.X86;
    using System.Security.Cryptography;

    /// <summary>
    /// Настройки для создания потока записи в файл (dest)
    /// источником является IConnection или ISSDConnection (src) 
    /// </summary>
    /// <param name="Name">полный путь</param>
    /// <param name="Append"> продолжить дописывать</param>
    /// <param name="FileFrom"> Чтение данных из (src) в (dest) было начято с этого значения</param>
    /// <param name="AppendFrom"> Если "Append = true" продолжить дописывать
    /// с этого значения если 0 то с конца файла (src) from = FileFrom + file.length
    /// Позиция (src) = from = AppendFrom, Позиция (dst) = AppendFrom - FileFrom  </param>
    public record FileReadOptions(string Name, bool Append = false, ulong FileFrom = 0, uint BufferSize = 0x100_0000);
    public record RamReadOptions(ulong From, ulong Total, bool ToEmpty = true, uint BufferSize = 0x100_0000);
    public record ComRamReadOptions : RamReadOptions
    {
        public enum Turbos
        {
            ts125K = 0,
            ts500K = 1,
            ts1M = 2,
        }
        public static readonly Dictionary<Turbos, int> TSD = new()
        {
            { Turbos.ts125K,125_000},
            {Turbos.ts500K,500_000},
            { Turbos.ts1M,1_000_000 },
        };
        public readonly ushort Adr;
        public readonly Turbos Turbo;
        public ComRamReadOptions(ushort Adr, ulong From, ulong Total, Turbos Turbo = Turbos.ts125K,bool ToEmpty = true, uint BufferSize = 0x8000)
            :base(From, Total, ToEmpty, BufferSize) 
        { 
            this.Adr = Adr;
            this.Turbo = Turbo; 
        }
}

    public static class ReadRam
    {
        /// <summary>
        /// Создает поток для записи и находит смещение (SrcFrom) источника данных (Если "Opt.Append = true" и SrcFrom = 0)
        /// </summary>
        /// <param name="Opt"></param>
        /// <param name="SrcFrom"></param>
        /// <param name="checkFileFrom">ulong "FileFrom" = int "first kadr" * kadrLength </param>
        /// <returns>Поток для записи и начало чтения для источника данных</returns>
        /// <exception cref="FlagsArgumentOutOfRangeException"></exception>
        /// <exception cref="FlagsOperationCanceledException"></exception>
        /// <exception cref="FlagsArgumentException"></exception>
        public static Stream CreateFileStream(FileReadOptions Opt, ref ulong SrcFrom, Action<int, ulong>? checkFileFrom = null )
        {
            string file = Opt.Name;
            FileStream? Res = null;

            if (File.Exists(file)) 
            {
                if (Opt.Append)
                {
                    Res = new FileStream(file, FileMode.Append, FileAccess.ReadWrite, FileShare.None, (int)Opt.BufferSize, useAsync: true);
                    if (SrcFrom != 0)
                    {
                        var filePos = (long)(SrcFrom - Opt.FileFrom);
                        if (Res.Length < filePos || filePos < 0)
                        {
                            string msg = string.Format(Resources.errAppendFromBad, Opt.FileFrom, Opt.FileFrom + (ulong)Res.Length, SrcFrom);
                            string param = Resources.prmFilePos;
                            throw new FlagsArgumentOutOfRangeException(param, filePos, msg);
                        }
                        if (checkFileFrom != null) checkFileFrom(ReadFirstKadr(Res), Opt.FileFrom);
                        Res.Seek(filePos, SeekOrigin.Begin);
                    }
                    else
                    {
                        SrcFrom = Opt.FileFrom + (ulong)Res.Length;
                        if (checkFileFrom != null) checkFileFrom(ReadFirstKadr(Res), Opt.FileFrom);
                        Res.Seek(0, SeekOrigin.End);
                    }
                }
                else
                {
                    string msg = string.Format(Resources.msgDeleteFile, file);
                    string capt = Resources.cptDeleteFile; 
                    var msgRes = MessageBox.Show(msg ,capt, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                    if (msgRes == MessageBoxResult.OK) 
                       File.Delete(file);
                    else 
                       throw new FlagsOperationCanceledException(capt);
                }
            }
            else
            {
                if (Opt.Append)
                {
                    string msg = string.Format(Resources.errAppendNotExistsFile, file);
                    string param = Resources.prmOptAppend;
                    throw new FlagsArgumentException(param, msg);
                }
            }
            return Res ?? new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, (int)Opt.BufferSize, useAsync: true);

            static int ReadFirstKadr(FileStream f)
            {
                f.Seek(0, SeekOrigin.Begin);
                using (BinaryReader r = new BinaryReader(f))
                {
                    return r.ReadInt32();
                }
            }
        }
        
        /// <summary>
        /// Чтение для Socket & Serial (src)
        /// </summary>
        /// <param name="src"></param>
        /// <param name="dest"></param>
        /// <param name="opt"></param>
        /// <param name="onProgress"></param>
        /// <returns></returns>
        public static Task<uint> Read(IConnection src, Stream dest, ComRamReadOptions opt, Action<ProgressData> onProgress, ILogger? logger = null)
        {
            src.CheckLock();

            return Task.Run(async () =>
            {
                try
                {
                    await src.Open();
                    try
                    {
                        var br = ComRamReadOptions.TSD[opt.Turbo];
                        if (opt.Turbo > 0)
                        {
                            ((AbstractConnection)src).dbg = "TURBO";
                            await Protocol.SetTurbo(src, (byte)opt.Turbo);
                            logger?.LogInformation($"{Thread.CurrentThread.ManagedThreadId} TURBO (END) start TURBO");
                            if (src is ISerialConnection) ((ISerialConnection)src).BaudRate = br;                            
                        }
                        int timOut = (int)opt.BufferSize * 1000 * 10 * 2 / br;
                        try
                        {
                            ((AbstractConnection)src).dbg = "ReadRam";
                            uint ac = (uint)Protocol.GetSizeOfAC(opt.Adr);
                            uint excl = ac + 2;
                            uint bufferSize = opt.BufferSize - excl;
                            uint from = (uint)opt.From;
                            uint total = (uint)opt.Total;
                            var progress = new Progress(opt.Total, opt.From);
                            progress.Start();
                            var old = progress.Elapsed;
                            try
                            {
                                while (from < opt.Total)
                                {
                                    uint cnt = from + bufferSize > total ? total - from : bufferSize;
                                    var (rsp, pro) = await Protocol.ReadRam(src, opt.Adr, from, cnt, timOut);
                                    progress.Update(cnt);
                                    //App.logger?.LogInformation($" cnt: {cnt:X}");
                                    await dest.WriteAsync(rsp.rxBuf, (int)ac, (int)cnt);
                                    if ((progress.Elapsed - old).TotalMilliseconds > 2000)
                                    {
                                        old = progress.Elapsed;
                                        var pd = progress.FindProgressData();
                                        logger?.LogWarning(pd.ToString());
                                        onProgress(pd);
                                    }
                                    from += cnt;
                                    if (opt.ToEmpty && SerialEnd(rsp.rxBuf))
                                    {
                                        break;
                                    }
                                }
                                return from - (uint)opt.From;
                            }
                            catch(ReadRamException)
                            {
                                return from - (uint)opt.From;
                            }
                        }
                        finally
                        {
                            try
                            {
                                if (opt.Turbo > 0)
                                {
                                    ((AbstractConnection)src).dbg = "UN TURBO";
                                    if (src.IsCanceled) Thread.Sleep(timOut); // wait last data sended
                                    logger?.LogInformation($"{Thread.CurrentThread.ManagedThreadId} finally UN TURBO(B)");
                                    await Protocol.SetTurbo(src, (byte)ComRamReadOptions.Turbos.ts125K);
                                    logger?.LogInformation($"{Thread.CurrentThread.ManagedThreadId} finally UN TURBO(E)");
                                }
                            }
                            finally 
                            {
                                if (src is ISerialConnection) 
                                    ((ISerialConnection)src).BaudRate = ComRamReadOptions.TSD[ComRamReadOptions.Turbos.ts125K];
                            }
                        }
                    }
                    finally
                    {
                        await src.Close();
                    }
                }
                finally
                {
                    logger?.LogInformation($"{Thread.CurrentThread.ManagedThreadId} UnLock");
                    src.UnLock();
                }
            });

            static unsafe bool SerialEnd(byte[] bytes, int MAXCnt = 256)
            {
                fixed (byte* p = &bytes[^(MAXCnt - 2)])
                {
                    uint* pint = (uint*)p;
                    for (int i = 0; i < MAXCnt / 4; i++)
                    {
                        if (*pint++ != 0xFFFF_FFFF) return false;
                    }
                    return true;
                }
            }
        }
        public static Task<ulong> Read(ISSDConnection src, Stream dest, RamReadOptions opt, Action<ProgressData> onProgress, ILogger? logger = null)
        {
            src.CheckLock();

            return Task.Run(() => 
            {
                try
                {
                    src.Open();

                    ulong ssdSize = (ulong)src.SSDSize;
                    var total = opt.Total;
                    var from = opt.From;

                    try
                    {
                        if (total > ssdSize)
                        {

                            string msg = string.Format(Resources.msgTotalBiggeSSD, opt.Total, ssdSize);
                            string capt = Resources.cptTotal;
                            var msgRes = MessageBox.Show(msg, capt, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                            if (msgRes == MessageBoxResult.OK)
                                total = ssdSize;
                            else
                                throw new FlagsOperationCanceledException(capt);
                        }
                        if (from >= total)
                        {
                            string msg = string.Format(Resources.errFromBiggeTotal, from, total);
                            string param = Resources.prmFrom;
                            throw new FlagsArgumentOutOfRangeException(param, opt.From, msg);
                        }
                        using FileStream f = new FileStream(src.handle!, FileAccess.Read);
                        f.Seek((long)from, SeekOrigin.Begin);

                        var toc = src.CancelTokenSource!.Token;

                        byte[] R = new byte[opt.BufferSize];
                        byte[] W = new byte[opt.BufferSize];

                        // begin: read from SSD
                        int cnt = (int) (from + opt.BufferSize > total ? total - from : opt.BufferSize);
                        Task<int> readTask = f.ReadAsync(R, 0, cnt, toc);
                        readTask.Wait();
                        int rcnt = readTask.Result;

                        Task writeTask;

                        var progress = new Progress(total,from);
                        progress.Start();
                        var old = progress.Elapsed;

                        // continue: read SSD write File
                        while (from + (uint) rcnt < total)
                        {
                            (R, W) = (W, R);
                            cnt = (int)(from + opt.BufferSize > total ? total - from : opt.BufferSize);
                            readTask = f.ReadAsync(R, 0, cnt, toc);
                            writeTask = dest.WriteAsync(W, 0, rcnt, toc);
                            Task.WhenAll(readTask, writeTask).Wait();

                            logger?.LogTrace($"read {rcnt:X}");

                            from += (uint)rcnt;
                            progress.Update((uint)rcnt);                           
                            if ((progress.Elapsed - old).TotalMilliseconds > 2000)
                            {
                                old = progress.Elapsed;
                                var pd = progress.FindProgressData();
                                logger?.LogWarning(pd.ToString());
                                onProgress(pd);
                            }
                            rcnt = readTask.Result;
                            // end if empty
                            if (opt.ToEmpty && SSDEnd(W))
                            {
                                return from - (uint)opt.From; 
                            }
                        }
                        // end: write last chunk to File
                        if (rcnt > 0)
                        {
                            writeTask = dest.WriteAsync(R, 0, rcnt, toc);
                            writeTask.Wait();
                            from += (uint)rcnt;
                            progress.Update((uint)rcnt);
                            onProgress(progress.FindProgressData());
                        }
                        return from - (uint)opt.From;
                    }
                    catch (OperationCanceledException e)
                    {
                        logger?.LogError(e.Message);
                        return from - (uint)opt.From;
                    }
                    catch (AggregateException e)
                    {
                        logger?.LogError(e.Message);
                        return from - (uint)opt.From;
                    }
                    finally
                    {
                        src.Close();
                    }
                }
                finally
                {
                    src.UnLock();
                }
            });
            static unsafe bool SSDEnd(byte[] bytes, int MAXCnt = 512)
            {
                fixed (byte* p = &bytes[^MAXCnt])
                {
                    uint* pint = (uint*)p;
                    for (int i = 0; i < MAXCnt / 4; i++)
                    {
                        if ((*pint != 0xFFFF_FFFF) || (*pint != 0)) return false;
                        pint++;
                    }
                    return true;
                }
            }

        }
    }
}
