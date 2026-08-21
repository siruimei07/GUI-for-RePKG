namespace WallpaperField.Models;

public sealed record MotionPreference(
    bool SystemAnimationsEnabled,
    bool ReducedMotionRequested)
{
    public bool MotionEnabled => SystemAnimationsEnabled && !ReducedMotionRequested;
}
