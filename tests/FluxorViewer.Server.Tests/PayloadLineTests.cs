using FluxorViewer.Server.Components;

namespace FluxorViewer.Server.Tests;

// UI-6: the payload strip's collapsed line.
public class PayloadLineTests
{
    [Fact]
    public void Strings_are_decoded_and_numbers_kept_as_received()
    {
        var line = PayloadLine.Of("""{"name":"café <b>","big":12345678901234567890123,"x":1.50}""");

        Assert.Equal("""{"name":"café <b>","big":12345678901234567890123,"x":1.50}""", line.Text);
    }

    [Fact]
    public void Indented_JSON_becomes_one_line()
    {
        Assert.Equal("""{"a":[1,2]}""", PayloadLine.Of("{\n  \"a\": [ 1, 2 ]\n}").Text);
    }

    [Theory]
    [InlineData("""{"a":1}""", true)]
    [InlineData("[1]", true)]
    [InlineData("{}", false)]
    [InlineData("[]", false)]
    [InlineData("42", false)]
    [InlineData("null", false)]
    public void Only_a_non_empty_object_or_array_can_expand(string json, bool canExpand) =>
        Assert.Equal(canExpand, PayloadLine.Of(json).CanExpand);

    [Fact]
    public void Long_Payloads_are_cut_with_an_ellipsis()
    {
        var json = $$"""{"s":"{{new string('x', 1000)}}"}""";

        var text = PayloadLine.Of(json).Text;

        Assert.Equal(PayloadLine.MaxLength + 1, text.Length);
        Assert.StartsWith("""{"s":"xxx""", text);
        Assert.EndsWith("x…", text);
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        // 😀 is two UTF-16 chars; place one so it straddles the cut.
        var json = $$"""{"s":"{{new string('x', PayloadLine.MaxLength - 7)}}😀😀"}""";

        var text = PayloadLine.Of(json).Text;

        Assert.False(char.IsHighSurrogate(text[^2]));
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void Malformed_JSON_shows_as_received_and_cannot_expand()
    {
        var line = PayloadLine.Of("""{"a":""");

        Assert.Equal("""{"a":""", line.Text);
        Assert.False(line.CanExpand);
    }
}
