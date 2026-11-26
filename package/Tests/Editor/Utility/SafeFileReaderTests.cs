using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;

namespace VRCRemoteTest.Tests
{
    public class SafeFileReaderTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "vrcrt-safe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var path in Directory.GetFileSystemEntries(_root))
            {
                // Links and FIFOs first: Directory.Delete would follow a link to a folder.
                if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    File.Delete(path);
                }
            }

            Directory.Delete(_root, true);
        }

        internal static bool Unix => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        internal static void Symlink(string target, string link)
        {
            if (!Unix)
            {
                Assert.Ignore("symbolic links are made with libc here");
            }

            Assert.AreEqual(0, symlink(target, link), "symlink failed");
        }

        [Test]
        public void An_ordinary_file_is_read()
        {
            var path = Path.Combine(_root, "a.json");
            File.WriteAllText(path, "{\"x\": \"手札\"}");

            Assert.AreEqual("{\"x\": \"手札\"}", SafeFileReader.ReadText(path, 1024, out var problem), problem);
        }

        [Test]
        public void A_missing_file_is_null()
        {
            Assert.IsNull(SafeFileReader.ReadText(Path.Combine(_root, "missing.json"), 1024, out var problem));
            StringAssert.Contains("does not exist", problem);
        }

        [Test]
        public void A_symbolic_link_is_refused_even_to_an_ordinary_file()
        {
            var target = Path.Combine(_root, "target.json");
            File.WriteAllText(target, "{}");
            var link = Path.Combine(_root, "link.json");
            Symlink(target, link);

            Assert.IsNull(SafeFileReader.ReadText(link, 1024, out var problem));
            Assert.IsNotNull(problem);
        }

        [Test]
        public void A_link_to_nothing_is_refused()
        {
            var link = Path.Combine(_root, "dangling.json");
            Symlink(Path.Combine(_root, "nothing"), link);

            Assert.IsNull(SafeFileReader.ReadText(link, 1024));
        }

        [Test, Timeout(10000)]
        public void A_fifo_is_refused_without_waiting_for_a_writer()
        {
            if (!Unix)
            {
                Assert.Ignore("FIFOs are made with libc here");
            }

            var fifo = Path.Combine(_root, "fifo.json");
            Assert.AreEqual(0, mkfifo(fifo, Convert.ToInt32("600", 8)), "mkfifo failed");

            Assert.IsNull(SafeFileReader.ReadText(fifo, 1024, out var problem));
            Assert.IsNotNull(problem);
        }

        [Test]
        public void A_device_is_refused()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Assert.Ignore("the fstat check is macOS's");
            }

            Assert.IsNull(SafeFileReader.ReadBytes("/dev/null", 1024, false, out var problem));
            StringAssert.Contains("not an ordinary file", problem);
            Assert.IsNull(SafeFileReader.ReadBytes("/dev/zero", 1024, true, out _));
        }

        [Test]
        public void An_own_ordinary_file_passes_the_fstat_check()
        {
            var path = Path.Combine(_root, "own.json");
            File.WriteAllText(path, "{}");

            CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("{}"), SafeFileReader.ReadBytes(path, 1024, false, out var problem), problem);

            var empty = Path.Combine(_root, "empty.json");
            File.WriteAllBytes(empty, new byte[0]);
            CollectionAssert.AreEqual(new byte[0], SafeFileReader.ReadBytes(empty, 1024, false, out problem), problem);
        }

        [Test]
        public void A_folder_is_refused()
        {
            var folder = Path.Combine(_root, "folder.json");
            Directory.CreateDirectory(folder);

            Assert.IsNull(SafeFileReader.ReadText(folder, 1024));
        }

        [Test]
        public void The_cap_is_inclusive_and_one_byte_more_is_refused()
        {
            var path = Path.Combine(_root, "capped.json");
            File.WriteAllBytes(path, new byte[100]);

            Assert.IsNotNull(SafeFileReader.ReadBytes(path, 100, false, out _));
            Assert.IsNull(SafeFileReader.ReadBytes(path, 99, false, out var problem));
            StringAssert.Contains("larger than", problem);
        }

        [Test]
        public void A_tail_is_the_last_bytes_whatever_the_size()
        {
            var path = Path.Combine(_root, "progress.jsonl");
            File.WriteAllText(path, "0123456789");

            Assert.AreEqual("6789", Encoding.ASCII.GetString(SafeFileReader.ReadBytes(path, 4, true, out _)));
            Assert.AreEqual("0123456789", Encoding.ASCII.GetString(SafeFileReader.ReadBytes(path, 64, true, out _)));
        }

        [Test]
        public void Text_that_is_not_UTF8_is_refused()
        {
            var path = Path.Combine(_root, "latin1.json");
            File.WriteAllBytes(path, new byte[] { (byte)'"', 0xff, 0xfe, (byte)'"' });

            Assert.IsNull(SafeFileReader.ReadText(path, 1024, out var problem));
            StringAssert.Contains("UTF-8", problem);
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int symlink(string target, string link);

        [DllImport("libc", SetLastError = true)]
        private static extern int mkfifo(string path, int mode);
    }
}
