namespace FluxorViewer.Protocol.Tests;

// IN-1: the Viewer accepts any minor version of its own major protocol version and rejects other majors.
public class ProtocolVersionTests
{
    [Theory]
    [InlineData(1, 0, 1, 0, true)]
    [InlineData(1, 0, 1, 7, true)]
    [InlineData(1, 0, 2, 0, false)]
    [InlineData(2, 3, 1, 3, false)]
    public void Compatible_only_when_major_versions_match(int major, int minor, int otherMajor, int otherMinor, bool expected)
    {
        Assert.Equal(expected, new ProtocolVersion(major, minor).IsCompatibleWith(new ProtocolVersion(otherMajor, otherMinor)));
    }

    [Fact]
    public void Formats_as_major_dot_minor()
    {
        Assert.Equal("1.0", new ProtocolVersion(1, 0).ToString());
    }
}
