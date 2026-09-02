using System.IO;
using System.Text;
using NUnit.Framework;

namespace VRCRemoteTest.Tests
{
    public class Sha256CalculatorTests
    {
        // No-BOM UTF-8: File.WriteAllText(path, s, Encoding.UTF8) prepends a
        // byte-order mark, which would change the hash relative to the plain
        // bytes a reference tool (e.g. `shasum`) computes over the same string.
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        [Test]
        public void ComputeHash_returns_correct_lowercase_hex()
        {
            var tempPath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempPath, "Hello, World!", Utf8NoBom);

                var hash = Sha256Calculator.ComputeHash(tempPath);

                // Reference value from `printf '%s' 'Hello, World!' | shasum -a 256`
                Assert.AreEqual(
                    "dffd6021bb2bd5b0af676290809ec3a53191dd81c7f70a4b28688a362182986f", hash);
            }
            finally
            {
                File.Delete(tempPath);
            }
        }

        [Test]
        public void ComputeHash_is_deterministic()
        {
            var tempPath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempPath, "deterministic content", Utf8NoBom);

                var first = Sha256Calculator.ComputeHash(tempPath);
                var second = Sha256Calculator.ComputeHash(tempPath);

                Assert.AreEqual(first, second);
            }
            finally
            {
                File.Delete(tempPath);
            }
        }

        [Test]
        public void ComputeHash_differs_for_different_content()
        {
            var pathA = Path.GetTempFileName();
            var pathB = Path.GetTempFileName();
            try
            {
                File.WriteAllText(pathA, "content A", Utf8NoBom);
                File.WriteAllText(pathB, "content B", Utf8NoBom);

                Assert.AreNotEqual(Sha256Calculator.ComputeHash(pathA), Sha256Calculator.ComputeHash(pathB));
            }
            finally
            {
                File.Delete(pathA);
                File.Delete(pathB);
            }
        }
    }
}
