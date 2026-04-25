namespace Exclr8.Terminal;

/// <summary>How <see cref="TerminalControl"/> handles a pending-write
/// queue that exceeds <see cref="TerminalControl.WriteQueueMaxBytes"/>.
/// Default is <see cref="None"/> — trusted PTYs can grow the queue
/// without bound, and the UI drain catches up at its own pace.</summary>
public enum WriteDropPolicy
{
    /// <summary>No cap. Producers always succeed; queue size grows
    /// with backpressure. Recommended for local PTYs and any
    /// trusted byte source.</summary>
    None,

    /// <summary>When the queue would exceed
    /// <see cref="TerminalControl.WriteQueueMaxBytes"/>, drop the
    /// oldest queued chunks until there's room. Preserves the
    /// freshest output — what the user actually wants to see — at
    /// the cost of a discontinuity in scrollback.</summary>
    OldestFirst,
}
