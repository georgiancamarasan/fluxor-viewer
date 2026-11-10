namespace FluxorViewer.Protocol;

/// <summary>
/// Version of the wire protocol. A change to <see cref="Major"/> is breaking; the Viewer
/// rejects Sessions whose major version differs from its own.
/// </summary>
public readonly record struct ProtocolVersion(int Major, int Minor)
{
    /// <summary>The version this build of the protocol speaks.</summary>
    public static ProtocolVersion Current { get; } = new(1, 0);

    public bool IsCompatibleWith(ProtocolVersion other) => Major == other.Major;

    public override string ToString() => $"{Major}.{Minor}";
}
