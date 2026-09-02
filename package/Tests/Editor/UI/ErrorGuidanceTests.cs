using System;
using System.Reflection;
using NUnit.Framework;

namespace VRCRemoteTest.Tests
{
    public class ErrorGuidanceTests
    {
        [Test]
        public void All_error_codes_have_guidance_except_the_explicit_exclusion_list()
        {
            var fields = typeof(ErrorCode).GetFields(BindingFlags.Public | BindingFlags.Static);

            foreach (var field in fields)
            {
                if (field.FieldType != typeof(string))
                {
                    continue;
                }

                var code = (string)field.GetValue(null);
                if (Array.IndexOf(ErrorGuidance.ExcludedFromCoverage, code) >= 0)
                {
                    continue;
                }

                Assert.IsNotNull(
                    ErrorGuidance.GetGuidance(code),
                    $"Missing ErrorGuidance entry for error code: {code}. " +
                    "Add one, or add it to ErrorGuidance.ExcludedFromCoverage if genuinely too generic.");
            }
        }

        [Test]
        public void GetGuidance_returns_null_for_unknown_code()
        {
            Assert.IsNull(ErrorGuidance.GetGuidance("NOT_A_REAL_CODE"));
        }

        [Test]
        public void GetGuidance_returns_null_for_null_input()
        {
            Assert.IsNull(ErrorGuidance.GetGuidance(null));
        }
    }
}
