/*
 * program.cs 
 * 
 * replicates the unix DU command
 * 
 *  Date        Author          Description
 *  ====        ======          ===========
 *  06-26-25    Craig           initial implementation
 *
 */

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DuFinal
{
    class Program
    {
        static bool showFiles = false;
        static bool summaryOnly = false;
        static bool useExactBytes = false;
        static bool useColor = false;
        static bool trackInodes = false;

        static HashSet<string> seen = new();

        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            EnableVirtualTerminal();

            List<string> targets = new();

            foreach (var arg in args)
            {
                switch (arg)
                {
                    case "-a": showFiles = true; break;
                    case "-s": summaryOnly = true; break;
                    case "-x": useExactBytes = true; break;
                    case "--color": useColor = true; break;
                    case "--track": trackInodes = true; break;
                    case "-?":
                    case "--help":
                        PrintHelp();
                        return;
                    default:
                        targets.Add(arg);
                        break;
                }
            }

            if (targets.Count == 0)
                targets.Add(Directory.GetCurrentDirectory());

            foreach (var target in targets)
            {
                if (!Directory.Exists(target))
                {
                    Console.Error.WriteLine($"du: cannot access '{target}': No such directory");
                    continue;
                }

                long total = StreamUsage(target);
                PrintDivider();
                PrintSizeLine(total, target, isTotal: true);
            }
        }

        static long StreamUsage(string dir)
        {
            long total = 0;

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var size = GetOnDiskSize(file);
                        if (!trackInodes || TrackFile(file))
                        {
                            total += size;
                            if (showFiles && !summaryOnly)
                                PrintSizeLine(size, file);
                        }
                    }
                    catch { }
                }

                foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        long subTotal = StreamUsage(sub);
                        total += subTotal;
                        if (!summaryOnly)
                            PrintSizeLine(subTotal, sub);
                    }
                    catch { }
                }
            }
            catch { }

            return total;
        }

        static bool TrackFile(string path)
        {
            try
            {
                using var handle = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var info = GetFileId(handle.SafeFileHandle);
                string key = $"{info.VolumeSerialNumber}-{info.FileIndexHigh}-{info.FileIndexLow}";
                if (seen.Contains(key)) return false;
                seen.Add(key);
                return true;
            }
            catch { return true; }
        }

        static void PrintDivider() => Console.WriteLine(new string('-', 12));

        static void PrintSizeLine(long size, string path, bool isTotal = false)
        {
            string sizeStr = useExactBytes
                ? size.ToString().PadLeft(12)
                : FormatSize(size).PadLeft(12);

            string color = useColor ? GetSizeColorGranular(size) : "";
            string reset = useColor ? "\x1b[0m" : "";

            Console.WriteLine($"{color}{sizeStr}{reset}  {path}");
        }

        static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return $"{size:0.##} {units[unit]}";
        }

        static string GetSizeColorGranular(long size)
        {
            // Units in bytes
            if (size >= 1L << 40) return "\x1b[91m"; // ≥ 1 TB (Bright Red)
            if (size >= 100L << 30) return "\x1b[31m"; // ≥ 100 GB (Red)
            if (size >= 10L << 30) return "\x1b[35m"; // ≥ 10 GB (Magenta)
            if (size >= 1L << 30) return "\x1b[33m";  // ≥ 1 GB (Yellow)
            if (size >= 100L << 20) return "\x1b[32m"; // ≥ 100 MB (Green)
            if (size >= 10L << 20) return "\x1b[36m";  // ≥ 10 MB (Cyan)
            if (size >= 1L << 20) return "\x1b[34m";   // ≥ 1 MB (Blue)
            return "\x1b[2m";                          // < 1 MB (Dim)
        }

        static void PrintHelp()
        {
            Console.WriteLine(@"
Usage: du [options] [dir...]
Options:
  -a         : include files
  -s         : summary only
  -x         : exact byte size
  --color    : enable ANSI color output
  --track    : track inodes to avoid double-counting (slower)
  -?         : show this help
");
        }

        static long GetOnDiskSize(string path)
        {
            uint lo = GetCompressedFileSizeW(path, out uint hi);
            ulong size = ((ulong)hi << 32) | lo;
            return (long)size;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

        [StructLayout(LayoutKind.Sequential)]
        struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(IntPtr hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        static BY_HANDLE_FILE_INFORMATION GetFileId(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle.DangerousGetHandle(), out var info))
                throw new IOException("Could not get file info.");
            return info;
        }

        static void EnableVirtualTerminal()
        {
            const int STD_OUTPUT_HANDLE = -11;
            var handle = GetStdHandle(STD_OUTPUT_HANDLE);
            GetConsoleMode(handle, out int mode);
            SetConsoleMode(handle, mode | 0x0004);
        }

        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr hConsoleHandle, out int lpMode);
        [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr hConsoleHandle, int dwMode);
    }
}
