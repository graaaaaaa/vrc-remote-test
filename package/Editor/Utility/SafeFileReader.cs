using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VRCRemoteTest
{
    /// <summary>
    /// Reads the runner's files (the claim, run.json, progress.jsonl, a checklist) without following
    /// a link and without blocking: a path that is itself a symbolic link, a folder, a FIFO, a
    /// socket or anything that is not an ordinary file is refused, and nothing past the cap is read.
    /// On macOS the file is opened with open(2) and O_NOFOLLOW | O_NONBLOCK (a link fails with
    /// ELOOP, a FIFO does not wait for a writer), and fstat(2) of that descriptor must show an
    /// ordinary file owned by this user (/dev/null, a FIFO or a socket is refused). On Linux, not a
    /// target, the same open and an lseek(2) that must give a size; elsewhere the attributes are
    /// checked before a FileStream opens it.
    /// </summary>
    internal static class SafeFileReader
    {
        /// <summary>The file's UTF-8 text, or null when it is missing, refused, too large or not UTF-8 (<paramref name="problem"/> says why).</summary>
        internal static string ReadText(string path, int maxBytes, out string problem)
        {
            var bytes = ReadBytes(path, maxBytes, false, out problem);
            if (bytes == null)
            {
                return null;
            }

            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (ArgumentException)
            {
                problem = Path.GetFileName(path) + " is not UTF-8";
                return null;
            }
        }

        internal static string ReadText(string path, int maxBytes) => ReadText(path, maxBytes, out _);

        /// <summary>
        /// The bytes of an ordinary file of at most <paramref name="maxBytes"/>, or, with
        /// <paramref name="tail"/>, its last <paramref name="maxBytes"/> bytes whatever its size.
        /// Null when missing or refused.
        /// </summary>
        internal static byte[] ReadBytes(string path, int maxBytes, bool tail, out string problem)
        {
            problem = null;
            if (string.IsNullOrEmpty(path) || maxBytes < 0)
            {
                problem = "no path";
                return null;
            }

            var name = Path.GetFileName(path);
            FileAttributes attributes;
            try
            {
                // Mono and .NET mark a symbolic link (the path itself, not its target) as a reparse point.
                attributes = File.GetAttributes(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException
                                       || ex is NotSupportedException)
            {
                problem = name + " does not exist";
                return null;
            }

            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
            {
                problem = name + " is not an ordinary file";
                return null;
            }

            try
            {
                return UnixLike ? ReadUnix(path, maxBytes, tail, out problem) : ReadOther(path, maxBytes, tail, out problem);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
            {
                problem = name + " could not be read: " + ex.Message;
                return null;
            }
        }

        // From the OS, not Application.platform: the window reads these files on a worker thread too.
        private static readonly bool IsMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        private static readonly bool UnixLike = IsMac || RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        private static byte[] ReadOther(string path, int maxBytes, bool tail, out string problem)
        {
            problem = null;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var length = stream.Length;
                if (!tail && length > maxBytes)
                {
                    problem = Path.GetFileName(path) + " is larger than " + maxBytes + " bytes";
                    return null;
                }

                var start = Math.Max(0, length - maxBytes);
                stream.Seek(start, SeekOrigin.Begin);
                return ReadUpTo(buffer => stream.Read(buffer, 0, buffer.Length), maxBytes, path, tail, out problem);
            }
        }

        private static byte[] ReadUnix(string path, int maxBytes, bool tail, out string problem)
        {
            problem = null;
            var name = Path.GetFileName(path);
            var flags = O_RDONLY | (IsMac ? MacNoFollow | MacNonBlock : LinuxNoFollow | LinuxNonBlock);
            var descriptor = open(path, flags);
            if (descriptor < 0)
            {
                var errno = Marshal.GetLastWin32Error();
                problem = errno == ENOENT ? name + " does not exist"
                    : errno == (IsMac ? MacELoop : LinuxELoop) ? name + " is a symbolic link"
                    : name + " could not be opened (errno " + errno + ")";
                return null;
            }

            try
            {
                if (IsMac && OwnRegularFile(descriptor) is string why)
                {
                    problem = name + " " + why;
                    return null;
                }

                // A FIFO, socket or terminal has no size: lseek fails with ESPIPE. (On macOS fstat has
                // already refused them; this is what is left of the check on Linux, not a target.)
                var length = lseek(descriptor, 0, SeekEnd);
                if (length < 0)
                {
                    problem = name + " is not an ordinary file";
                    return null;
                }

                if (!tail && length > maxBytes)
                {
                    problem = name + " is larger than " + maxBytes + " bytes";
                    return null;
                }

                if (lseek(descriptor, Math.Max(0, length - maxBytes), SeekSet) < 0)
                {
                    problem = name + " could not be read";
                    return null;
                }

                return ReadUpTo(buffer =>
                {
                    while (true)
                    {
                        var read = (long)read_(descriptor, buffer, (IntPtr)buffer.Length);
                        if (read >= 0)
                        {
                            return (int)read;
                        }

                        var errno = Marshal.GetLastWin32Error();
                        if (errno != EINTR)
                        {
                            throw new IOException(name + " could not be read (errno " + errno + ")");
                        }
                    }
                }, maxBytes, path, tail, out problem);
            }
            finally
            {
                close(descriptor);
            }
        }

        /// <summary>Reads until the end or one byte past the cap (the file grew since it was measured: refused, or cut for a tail).</summary>
        private static byte[] ReadUpTo(Func<byte[], int> read, int maxBytes, string path, bool tail, out string problem)
        {
            problem = null;
            var output = new MemoryStream();
            var buffer = new byte[Math.Min(64 * 1024, maxBytes + 1)];
            while (output.Length <= maxBytes)
            {
                var count = read(buffer);
                if (count <= 0)
                {
                    break;
                }

                output.Write(buffer, 0, count);
            }

            if (output.Length > maxBytes)
            {
                if (!tail)
                {
                    problem = Path.GetFileName(path) + " is larger than " + maxBytes + " bytes";
                    return null;
                }

                var all = output.ToArray();
                var cut = new byte[maxBytes];
                Array.Copy(all, 0, cut, 0, maxBytes);
                return cut;
            }

            return output.ToArray();
        }

        // Darwin's struct stat with 64-bit inodes (the default): st_mode is a uint16 at offset 4,
        // st_uid a uint32 at offset 16; the whole struct is 144 bytes. Its symbol is fstat on arm64
        // and fstat$INODE64 on x86_64 (where plain fstat is the old 32-bit-inode layout).
        private const int DarwinStatSize = 144;
        private const int DarwinModeOffset = 4;
        private const int DarwinUidOffset = 16;
        private const int S_IFMT = 0xF000;
        private const int S_IFREG = 0x8000;

        /// <summary>
        /// Null when the open descriptor is an ordinary file owned by this user (fstat of the same
        /// descriptor, so nothing can be swapped in between); else why not. macOS only.
        /// </summary>
        private static string OwnRegularFile(int descriptor)
        {
            var buffer = new byte[DarwinStatSize + 112];
            var status = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? fstat_x64(descriptor, buffer)
                : fstat_arm64(descriptor, buffer);
            if (status != 0)
            {
                return "could not be checked (errno " + Marshal.GetLastWin32Error() + ")";
            }

            var mode = BitConverter.ToUInt16(buffer, DarwinModeOffset);
            if ((mode & S_IFMT) != S_IFREG)
            {
                return "is not an ordinary file";
            }

            return BitConverter.ToUInt32(buffer, DarwinUidOffset) == getuid() ? null : "is not owned by this user";
        }

        private const int O_RDONLY = 0;
        private const int MacNonBlock = 0x0004;
        private const int MacNoFollow = 0x0100;
        private const int LinuxNonBlock = 0x0800;
        private const int LinuxNoFollow = 0x20000;
        private const int ENOENT = 2;
        private const int EINTR = 4;
        private const int MacELoop = 62;
        private const int LinuxELoop = 40;
        private const int SeekSet = 0;
        private const int SeekEnd = 2;

        [DllImport("libc", SetLastError = true)]
        private static extern int open(string path, int flags);

        [DllImport("libc", EntryPoint = "read", SetLastError = true)]
        private static extern IntPtr read_(int descriptor, byte[] buffer, IntPtr count);

        [DllImport("libc", SetLastError = true)]
        private static extern long lseek(int descriptor, long offset, int whence);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int descriptor);

        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        private static extern int fstat_arm64(int descriptor, byte[] buffer);

        [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
        private static extern int fstat_x64(int descriptor, byte[] buffer);

        [DllImport("libc")]
        private static extern uint getuid();
    }
}
