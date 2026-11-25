using System.Text;
using FluentAssertions;
using VRCRemoteTest.Bridge.Lan;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>The optional body of a single-client close or launch (docs/plans/r1b-contract.md §1.3).</summary>
public class SlotPreconditionTests
{
    private const string Json = "application/json";
    private const string InstanceId = "0123456789abcdef0123456789abcdef";

    private static string Expect(string? replace = null, string? with = null, string? extra = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["bridgeInstanceId"] = $"\"{InstanceId}\"",
            ["hostGeneration"] = "3",
            ["activeConfigEtag"] = "\"a1\"",
            ["savedConfigEtag"] = "null",
            ["operationRevision"] = "17",
            ["buildId"] = "\"20261126T000000000Z-aabbccdd\"",
            ["clientGeneration"] = "1234",
        };
        if (replace is not null)
        {
            if (with is null) fields.Remove(replace);
            else fields[replace] = with;
        }

        var body = string.Join(",", fields.Select(f => $"\"{f.Key}\":{f.Value}"));
        return "{" + body + (extra ?? "") + "}";
    }

    private static (SlotPrecondition?, string?, IReadOnlyList<string>?) Parse(string body, string? type = Json) =>
        SlotPrecondition.Parse(Encoding.UTF8.GetBytes(body), type);

    [Fact]
    public void An_empty_body_is_the_old_request()
    {
        var (precondition, operationId, problems) = SlotPrecondition.Parse([], null);

        precondition.Should().BeNull();
        operationId.Should().BeNull();
        problems.Should().BeNull();
    }

    [Fact]
    public void A_complete_body_is_read()
    {
        var (precondition, operationId, problems) =
            Parse("{\"expect\":" + Expect() + ",\"operationId\":\"run-1_close\"}", "application/json; charset=utf-8");

        problems.Should().BeNull();
        operationId.Should().Be("run-1_close");
        precondition.Should().Be(new SlotPrecondition(InstanceId, 3, "a1", null, 17, "20261126T000000000Z-aabbccdd", 1234));
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData(null)]
    [InlineData("application/jsonx")]
    public void A_body_of_another_type_is_refused(string? type)
    {
        Parse("{\"expect\":" + Expect() + "}", type).Item3.Should().NotBeNull();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"expect\":[]}")]
    [InlineData("{\"expect\":null}")]
    public void A_body_without_an_expect_object_is_refused(string body)
    {
        Parse(body).Item3.Should().NotBeNull();
    }

    [Fact]
    public void Missing_extra_and_repeated_keys_are_refused()
    {
        Parse("{\"expect\":" + Expect("clientGeneration") + "}").Item3.Should().ContainMatch("*clientGeneration is missing*");
        Parse("{\"expect\":" + Expect(extra: ",\"slot\":0") + "}").Item3.Should().ContainMatch("*Unknown key 'slot'*");
        Parse("{\"expect\":" + Expect(extra: ",\"hostGeneration\":4") + "}").Item3.Should().ContainMatch("*more than once*");
        Parse("{\"expect\":" + Expect() + ",\"expect\":" + Expect() + "}").Item3.Should().ContainMatch("*more than once*");
        Parse("{\"expect\":" + Expect() + ",\"force\":true}").Item3.Should().ContainMatch("*Unknown key 'force'*");
    }

    [Theory]
    [InlineData("bridgeInstanceId", "\"0123456789ABCDEF0123456789ABCDEF\"")]
    [InlineData("bridgeInstanceId", "null")]
    [InlineData("hostGeneration", "0")]
    [InlineData("hostGeneration", "true")]
    [InlineData("hostGeneration", "\"3\"")]
    [InlineData("hostGeneration", "3.0")]
    [InlineData("operationRevision", "-1")]
    [InlineData("operationRevision", "1e2")]
    [InlineData("activeConfigEtag", "\"\"")]
    [InlineData("activeConfigEtag", "5")]
    [InlineData("buildId", "\"../x\"")]
    [InlineData("clientGeneration", "0")]
    [InlineData("clientGeneration", "null")]
    [InlineData("clientGeneration", "99999999999999999999")]
    public void A_value_of_the_wrong_type_or_form_is_refused(string key, string value)
    {
        Parse("{\"expect\":" + Expect(key, value) + "}").Item3.Should().NotBeNull();
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"a b\"")]
    [InlineData("5")]
    public void A_bad_operation_id_is_refused(string id)
    {
        Parse("{\"expect\":" + Expect() + ",\"operationId\":" + id + "}").Item3.Should().NotBeNull();
    }

    [Fact]
    public void A_body_over_the_limit_is_refused()
    {
        var body = "{\"expect\":" + Expect() + ",\"operationId\":\"x\"" + new string(' ', SlotPrecondition.MaxBodyBytes) + "}";
        Parse(body).Item3.Should().ContainMatch("*larger than*");
    }

    [Fact]
    public void Mismatched_names_every_process_value_that_differs()
    {
        var expected = new SlotPrecondition(InstanceId, 3, "a1", null, 17, "b1", 1234);
        var current = new SlotPrecondition(InstanceId, 4, "a1", "s1", 18, null, 0);

        expected.Mismatched(current).Should().Equal("hostGeneration", "savedConfigEtag", "operationRevision", "buildId");
        expected.Mismatched(expected with { ClientGeneration = 1 }).Should().BeEmpty("the client generation is the slot's, checked later");
    }
}
