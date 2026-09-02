using NUnit.Framework;

namespace VRCRemoteTest.Tests
{
    public class RemoteTestWindowFilterTests
    {
        private static readonly string[] SampleLines =
        {
            "2026.09.02 23:22:13 Error      - Something went wrong",
            "2026.09.02 23:22:14 Log        - Udon behaviour initialized",
            "2026.09.02 23:22:15 Exception  - NullReferenceException at Foo.Bar",
            "2026.09.02 23:22:16 Warning    - Shader compilation warning",
            "2026.09.02 23:22:17 Log        - Nothing special here",
        };

        [Test]
        public void FilterLogLines_returns_all_lines_for_All_category()
        {
            var result = RemoteTestWindow.FilterLogLines(SampleLines, "All");

            Assert.AreEqual(SampleLines.Length, result.Length);
        }

        [Test]
        public void FilterLogLines_returns_all_lines_for_null_or_empty_category()
        {
            Assert.AreEqual(SampleLines.Length, RemoteTestWindow.FilterLogLines(SampleLines, null).Length);
            Assert.AreEqual(SampleLines.Length, RemoteTestWindow.FilterLogLines(SampleLines, string.Empty).Length);
        }

        [Test]
        public void FilterLogLines_filters_by_Error_category()
        {
            var result = RemoteTestWindow.FilterLogLines(SampleLines, "Error");

            Assert.AreEqual(1, result.Length);
            StringAssert.Contains("Something went wrong", result[0]);
        }

        [Test]
        public void FilterLogLines_filters_by_Exception_category()
        {
            var result = RemoteTestWindow.FilterLogLines(SampleLines, "Exception");

            Assert.AreEqual(1, result.Length);
            StringAssert.Contains("NullReferenceException", result[0]);
        }

        [Test]
        public void FilterLogLines_filters_by_Udon_category_via_substring_match()
        {
            var result = RemoteTestWindow.FilterLogLines(SampleLines, "Udon");

            Assert.AreEqual(1, result.Length);
            StringAssert.Contains("Udon behaviour initialized", result[0]);
        }

        [Test]
        public void FilterLogLines_filters_by_Shader_category()
        {
            var result = RemoteTestWindow.FilterLogLines(SampleLines, "Shader");

            Assert.AreEqual(1, result.Length);
            StringAssert.Contains("Shader compilation warning", result[0]);
        }

        [Test]
        public void FilterLogLines_filters_by_Warning_category()
        {
            var result = RemoteTestWindow.FilterLogLines(SampleLines, "Warning");

            Assert.AreEqual(1, result.Length);
            StringAssert.Contains("Shader compilation warning", result[0]);
        }

        [Test]
        public void FilterLogLines_match_is_case_insensitive()
        {
            var result = RemoteTestWindow.FilterLogLines(SampleLines, "error");

            Assert.AreEqual(1, result.Length);
        }

        [Test]
        public void FilterLogLines_returns_empty_array_when_no_lines_match()
        {
            var result = RemoteTestWindow.FilterLogLines(SampleLines, "NoSuchCategory");

            Assert.AreEqual(0, result.Length);
        }

        [Test]
        public void FilterLogLines_returns_empty_array_for_null_input()
        {
            var result = RemoteTestWindow.FilterLogLines(null, "All");

            Assert.AreEqual(0, result.Length);
        }
    }
}
