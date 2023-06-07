using System.Runtime.InteropServices;

namespace FFmpeg.Helper
{
    static internal class NativeMethods
    {
        [DllImport("Kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
        internal static unsafe extern void MoveMemory(byte* dest, byte* src, int size);

        [DllImport("Kernel32.dll")]
        static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

        [DllImport("Kernel32.dll")]
        static extern bool QueryPerformanceFrequency(out long lpFrequency);


        public static void uSleep(long waitTime) 
        {
            long time1 = 0, time2 = 0, freq = 0;

            QueryPerformanceCounter(out time1);
            QueryPerformanceFrequency(out freq);

            do
            {
                QueryPerformanceCounter(out time2);
            } while ((time2 - time1) < waitTime);
        }
    }
}
