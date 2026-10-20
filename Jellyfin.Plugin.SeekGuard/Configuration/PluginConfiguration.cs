using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SeekGuard.Configuration;

/// <summary>
/// Plugin settings.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether resume points are guarded.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the smallest position change, in seconds, that counts as a seek.
    /// </summary>
    public int MinJumpSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets how many seconds must be played after a seek before the new position is saved.
    /// </summary>
    public int ConfirmSeconds { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether skipping an intro, recap, credits or other media segment
    /// is treated as deliberate and saved right away.
    /// </summary>
    public bool TrustSegmentSkips { get; set; } = true;
}
