using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4DecodeDisplayOrderTests
{
    [Fact]
    public void Empty_list_stays_empty_in_both_modes()
    {
        var empty = Array.Empty<string>();

        Assert.Same(empty, Ft4DecodeDisplayOrder.ForDisplay(empty, newestAtBottom: false));
        Assert.Same(empty, Ft4DecodeDisplayOrder.ForDisplay(empty, newestAtBottom: true));
    }

    [Fact]
    public void Newest_first_storage_stays_at_the_top_when_the_option_is_off()
    {
        IReadOnlyList<string> newestFirst = ["new", "old"];

        var display = Ft4DecodeDisplayOrder.ForDisplay(newestFirst, newestAtBottom: false);

        Assert.Same(newestFirst, display);
        Assert.Equal("new", display[0]);
        Assert.Equal("old", display[1]);
    }

    [Fact]
    public void Newest_first_storage_moves_to_the_bottom_when_the_option_is_on()
    {
        IReadOnlyList<string> newestFirst = ["new", "old"];

        var display = Ft4DecodeDisplayOrder.ForDisplay(newestFirst, newestAtBottom: true);

        Assert.Equal(2, display.Count);
        Assert.Equal("old", display[0]);
        Assert.Equal("new", display[1]);
    }

    [Fact]
    public void Single_row_is_not_copied_when_reversing()
    {
        IReadOnlyList<string> one = ["only"];

        Assert.Same(one, Ft4DecodeDisplayOrder.ForDisplay(one, newestAtBottom: true));
    }
}
