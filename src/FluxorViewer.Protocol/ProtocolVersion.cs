namespace FluxorViewer.Protocol;

/// <summary>
/// Version of the wire protocol. A change to <see cref="Major"/> is breaking; the Viewer
/// rejects Sessions whose major version differs from its own.
/// </summary>
public readonly record struct ProtocolVersion(int Major, int Minor)
{
    /// <summary>The version this build of the protocol speaks.</summary>
    public static ProtocolVersion Current { get; } = new(1, 0);

    /// <summary>Whether a peer speaking <paramref name="other"/> can talk to this one: the major versions match.</summary>
    public bool IsCompatibleWith(ProtocolVersion other) => Major == other.Major;

    /// <summary>The version as <c>Major.Minor</c>, e.g. <c>1.0</c>.</summary>
    public override string ToString() => $"{Major}.{Minor}";
}
