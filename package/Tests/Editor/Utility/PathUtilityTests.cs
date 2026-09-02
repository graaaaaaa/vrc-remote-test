using NUnit.Framework;

namespace VRCRemoteTest.Tests
{
    public class PathUtilityTests
    {
        [TestCase("20260901T112522481Z-a91f02cc.vrcw")]
        [TestCase("test.vrcw")]
        [TestCase("vrc-remote-20260901T112522481Z-a91f02cc.vrcw")]
        public void IsSafeBasename_accepts_valid_vrcw_names(string fileName)
        {
            Assert.IsTrue(PathUtility.IsSafeBasename(fileName));
        }

        [TestCase("../../evil.vrcw")]
        [TestCase("sub/dir.vrcw")]
        [TestCase("sub\\dir.vrcw")]
        [TestCase("..\\evil.vrcw")]
        [TestCase("/etc/passwd.vrcw")]
        [TestCase("C:\\Windows\\evil.vrcw")]
        [TestCase("a:b.vrcw")]
        public void IsSafeBasename_rejects_path_traversal_and_separators(string fileName)
        {
            Assert.IsFalse(PathUtility.IsSafeBasename(fileName));
        }

        [TestCase("test.txt")]
        [TestCase("test")]
        [TestCase("test.vrcw.exe")]
        public void IsSafeBasename_rejects_non_vrcw_extension(string fileName)
        {
            Assert.IsFalse(PathUtility.IsSafeBasename(fileName));
        }

        [TestCase("")]
        [TestCase(null)]
        public void IsSafeBasename_rejects_empty_and_null(string fileName)
        {
            Assert.IsFalse(PathUtility.IsSafeBasename(fileName));
        }

        [TestCase("CON.vrcw")]
        [TestCase("con.vrcw")]
        [TestCase("PRN.vrcw")]
        [TestCase("COM1.vrcw")]
        [TestCase("LPT1.vrcw")]
        public void IsSafeBasename_rejects_windows_reserved_names(string fileName)
        {
            Assert.IsFalse(PathUtility.IsSafeBasename(fileName));
        }
    }
}
