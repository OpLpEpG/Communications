using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Connections
{
    public record struct ProgressData(double LocalSpeed, double GlobalSpeed, double ProgressPct, TimeSpan Elapsed, TimeSpan Remaining)
    {
        public static string SpeedToStr(double v)
        {
            return v switch
            {
                > 102.4 * 1024 => $"{v / (1024 * 1024):F1}MB/s",
                > 102.4 => $"{v / 1024:F1}KB/s",
                _ => $"{v:F1}B/s"
            };
        }
        public string localVelosity { get => SpeedToStr(LocalSpeed); }
        public string globalVelosity { get => SpeedToStr(GlobalSpeed); }
        public string progress { get => $"{ProgressPct:F1}%"; }
        public string elapsed { get => Elapsed.ToString(@"hh\:mm\:ss"); }
        public string remaining { get => Remaining.ToString(@"hh\:mm\:ss"); }
        public override string ToString() => $"{globalVelosity} {localVelosity} {ProgressPct:F1}% {elapsed} {remaining}";
    }
    public class Progress
    {
        public readonly ulong total;
        public ulong inputBytes = 0;
        public uint localinputBytes = 0;
        private readonly Stopwatch stopwatch = new();
        public Progress(ulong Total) => total = Total;
        public Progress(ulong Total, ulong From)
        {
            total = Total;
            inputBytes = From;
        }
        public void Start() 
        { 
            stopwatch.Start(); 
            OldElapsed = stopwatch.Elapsed;
        }
        public void Update(uint cnt, uint exclude = 0) => localinputBytes += cnt - exclude;
        public TimeSpan Elapsed => stopwatch.Elapsed;
        private TimeSpan OldElapsed;
        public ProgressData FindProgressData() 
        {
            TimeSpan curEl = Elapsed;
            var dt = curEl - OldElapsed;
            OldElapsed = curEl;
            inputBytes += localinputBytes;
            double LocalSpeed = (double)localinputBytes * 1_000_000 / dt.TotalMicroseconds;
            localinputBytes = 0;
            double GlobalSpeed = inputBytes * 1_000_000 / curEl.TotalMicroseconds;
            double Progress = ((double)inputBytes)*100/total;
            long trem = (long)((total - inputBytes) * TimeSpan.TicksPerSecond / GlobalSpeed);
            TimeSpan Remaining = new (trem);
            return new(LocalSpeed, GlobalSpeed, Progress, curEl, Remaining);
        }
    }
}
