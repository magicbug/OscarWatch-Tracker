using OscarWatch.Core.Models;

namespace OscarWatch.Tests;

public sealed class SidebarDetachVisibilityTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void Docked_visible_only_when_expanded_and_not_detached(
        bool expanded,
        bool detached,
        bool expectedDockedVisible)
    {
        Assert.Equal(expectedDockedVisible, expanded && !detached);
    }

    [Fact]
    public void PassesDetachedWindowDefaults_rejects_undersized_saved_bounds()
    {
        var settings = new AppSettings
        {
            PassesDetachedWindowWidth = 100,
            PassesDetachedWindowHeight = 50,
            PassesDetachedWindowX = 10,
            PassesDetachedWindowY = 20
        };

        Assert.False(PassesDetachedWindowDefaults.TryGetSavedSize(settings, out _, out _));
        Assert.True(PassesDetachedWindowDefaults.TryGetSavedPosition(settings, out var x, out var y));
        Assert.Equal(10, x);
        Assert.Equal(20, y);
    }

    [Fact]
    public void PassesDetachedWindowDefaults_accepts_valid_saved_size()
    {
        var settings = new AppSettings
        {
            PassesDetachedWindowWidth = PassesDetachedWindowDefaults.MinWidth,
            PassesDetachedWindowHeight = PassesDetachedWindowDefaults.MinHeight
        };

        Assert.True(PassesDetachedWindowDefaults.TryGetSavedSize(settings, out var width, out var height));
        Assert.Equal(PassesDetachedWindowDefaults.MinWidth, width);
        Assert.Equal(PassesDetachedWindowDefaults.MinHeight, height);
    }

    [Fact]
    public void RovesDetachedWindowDefaults_rejects_undersized_saved_bounds()
    {
        var settings = new AppSettings
        {
            RovesDetachedWindowWidth = 100,
            RovesDetachedWindowHeight = 50,
            RovesDetachedWindowX = 12,
            RovesDetachedWindowY = 24
        };

        Assert.False(RovesDetachedWindowDefaults.TryGetSavedSize(settings, out _, out _));
        Assert.True(RovesDetachedWindowDefaults.TryGetSavedPosition(settings, out var x, out var y));
        Assert.Equal(12, x);
        Assert.Equal(24, y);
    }

    [Fact]
    public void RovesDetachedWindowDefaults_accepts_valid_saved_size()
    {
        var settings = new AppSettings
        {
            RovesDetachedWindowWidth = RovesDetachedWindowDefaults.MinWidth,
            RovesDetachedWindowHeight = RovesDetachedWindowDefaults.MinHeight
        };

        Assert.True(RovesDetachedWindowDefaults.TryGetSavedSize(settings, out var width, out var height));
        Assert.Equal(RovesDetachedWindowDefaults.MinWidth, width);
        Assert.Equal(RovesDetachedWindowDefaults.MinHeight, height);
    }

    [Fact]
    public void AppSettings_sidebar_detach_defaults()
    {
        var settings = new AppSettings();
        Assert.True(settings.PassesExpanded);
        Assert.False(settings.IsPassesDetached);
        Assert.Null(settings.PassesDetachedWindowWidth);
        Assert.Null(settings.PassesDetachedWindowHeight);
        Assert.Null(settings.PassesDetachedWindowX);
        Assert.Null(settings.PassesDetachedWindowY);

        Assert.True(settings.HamsAtRovesExpanded);
        Assert.False(settings.IsRovesDetached);
        Assert.Null(settings.RovesDetachedWindowWidth);
        Assert.Null(settings.RovesDetachedWindowHeight);
        Assert.Null(settings.RovesDetachedWindowX);
        Assert.Null(settings.RovesDetachedWindowY);
    }
}
