using Connections.Interface;
using ExceptionExtensions;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Connections
{
    using LARGE_INTEGER = Int64;
    using DWORD = UInt32;
    using static Connections.IoCtl;

    public static class IoCtl
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern SafeFileHandle CreateFile(
        string lpFileName,
        DWORD dwDesiredAccess,
        [MarshalAs(UnmanagedType.U4)] FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        DWORD dwCreationDisposition,
        DWORD dwFlagsAndAttributes,
        IntPtr hTemplateFile);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize,
        ref uint lpBytesReturned, IntPtr lpOverlapped);

        public enum MEDIA_TYPE : int
        {
            Unknown = 0,
            F5_1Pt2_512 = 1,
            F3_1Pt44_512 = 2,
            F3_2Pt88_512 = 3,
            F3_20Pt8_512 = 4,
            F3_720_512 = 5,
            F5_360_512 = 6,
            F5_320_512 = 7,
            F5_320_1024 = 8,
            F5_180_512 = 9,
            F5_160_512 = 10,
            RemovableMedia = 11,
            FixedMedia = 12,
            F3_120M_512 = 13,
            F3_640_512 = 14,
            F5_640_512 = 15,
            F5_720_512 = 16,
            F3_1Pt2_512 = 17,
            F3_1Pt23_1024 = 18,
            F5_1Pt23_1024 = 19,
            F3_128Mb_512 = 20,
            F3_230Mb_512 = 21,
            F8_256_128 = 22,
            F3_200Mb_512 = 23,
            F3_240M_512 = 24,
            F3_32M_512 = 25
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISK_GEOMETRY
        {
            internal LARGE_INTEGER Cylinders;
            internal MEDIA_TYPE MediaType;
            internal DWORD TracksPerCylinder;
            internal DWORD SectorsPerTrack;
            internal DWORD BytesPerSector;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISK_GEOMETRY_EX
        {
            internal DISK_GEOMETRY Geometry;
            internal LARGE_INTEGER DiskSize;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            internal byte[] Data;
        }
        static DWORD CTL_CODE(DWORD DeviceType, DWORD Function, DWORD Method, DWORD Access)
        {
            return (((DeviceType) << 16) | ((Access) << 14) | ((Function) << 2) | (Method));
        }
        public const DWORD
            DISK_BASE = 0x00000007,
            METHOD_BUFFERED = 0,
            FILE_ANY_ACCESS = 0;


        public const DWORD
            FILE_FLAG_OVERLAPPED = 0x40000000,
            GENERIC_READ = 0x80000000,
            FILE_SHARE_WRITE = 0x2,
            FILE_SHARE_READ = 0x1,
            OPEN_EXISTING = 0x3;

        public static readonly DWORD DISK_GET_DRIVE_GEOMETRY_EX =
            CTL_CODE(DISK_BASE, 0x0028, METHOD_BUFFERED, FILE_ANY_ACCESS);

        public static readonly DWORD DISK_GET_DRIVE_GEOMETRY =
            CTL_CODE(DISK_BASE, 0, METHOD_BUFFERED, FILE_ANY_ACCESS);

        public static SafeFileHandle CreateFile(char Letter, bool Overlaped)
        {
            return CreateFile($"\\\\.\\{Letter}:",
                   GENERIC_READ,
                   FileShare.ReadWrite,
                   lpSecurityAttributes: default,
                   dwCreationDisposition: OPEN_EXISTING,
                   dwFlagsAndAttributes: Overlaped ? FILE_FLAG_OVERLAPPED : 0,
                   default);
        }

        public unsafe static DISK_GEOMETRY_EX GetSSDGeometry(char Letter)
        {

            using (var hDevice = CreateFile(Letter, false))
            {
                if (null == hDevice || hDevice.IsInvalid)
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                var nOutBufferSize = Marshal.SizeOf<DISK_GEOMETRY_EX>();
                var lpOutBuffer = Marshal.AllocHGlobal(nOutBufferSize);
                var lpBytesReturned = default(DWORD);
                var NULL = IntPtr.Zero;

                var result =
                    DeviceIoControl(
                        hDevice, DISK_GET_DRIVE_GEOMETRY_EX,
                        NULL, 0,
                        lpOutBuffer, nOutBufferSize,
                        ref lpBytesReturned, NULL
                        );

                if (!result)
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                var res = Marshal.PtrToStructure<DISK_GEOMETRY_EX>(lpOutBuffer);
                Marshal.FreeHGlobal(lpOutBuffer);
                return res;
            }
        }
    }

    public class SSDConn : ISSDConnection
    {
        //static SSDConn()
        //{
        //    ConnectionTypes.Add(typeof(SSDConn));
        //}
        public override string? ToString()
        {
            return $"{Letter}:";
        }
        private object _lock = new object();
        private DISK_GEOMETRY_EX? _geometry; 

        private int running = 0;
        public bool Lock()
        {
            int r = Interlocked.CompareExchange(ref running, 1, 0);
            return r == 0;
        }
        public void UnLock()
        {
            Interlocked.Exchange(ref running, 0);
        }
        public bool IsLocked { get { return running == 1; } }

        public virtual void Cancel()
        {
                _calcelCtx.Cancel();
        }
        public bool IsCanceled { get => _calcelCtx.IsCancellationRequested; }

        private bool _isopen;
        public Task Open(int timout = 10000)
        {
            lock (_lock) 
            {
                _geometry = GetSSDGeometry(Letter);
                _handle = CreateFile(Letter, true);
                if (_calcelCtx.IsCancellationRequested)
                {
                    _calcelCtx.Dispose();
                    _calcelCtx = new CancellationTokenSource();
                }
                _isopen = true;
            }
            return Task.CompletedTask;
        }
        public Task Close(int timout = 5000)
        {
            lock (_lock)
            {
                _geometry = null;
                _isopen = false;
                _handle?.Close();
                _handle = null;
            }
            return Task.CompletedTask;
        }
        public bool IsOpen { get { return _isopen; } }

        private char _letter = 'Z';
        public char Letter { get=>_letter;
            set 
            {
                if (_letter == value) return;
                if (IsOpen || IsLocked) throw new FlagsException($"Соединение {_letter}: открыто", Dialog:true, LogStack:false);
                _letter = value;
            } 
        }

        public long SSDSize { get => _geometry.HasValue ? _geometry.Value.DiskSize : 0; }
        public uint SectorSize { get => _geometry.HasValue ? _geometry.Value.Geometry.BytesPerSector : 0; }
        private SafeFileHandle? _handle;
        public SafeFileHandle? handle { get => _handle; }

        private CancellationTokenSource _calcelCtx = new CancellationTokenSource();
        public CancellationTokenSource CancelTokenSource { get => _calcelCtx; }

        public void Dispose()
        {
           if (_isopen) Close();
        }
    }
}
