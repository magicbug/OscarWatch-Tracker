using OscarWatch.Core.CwDecode;
using Xunit;

namespace OscarWatch.Tests.CwDecode;

public sealed class CwWorkedLookupTests
{
    private static readonly HashSet<string> Calls = new(StringComparer.Ordinal) { "M0NPT", "G4ABC" };
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal) { "IO92", "IO91" };

    [Theory]
    [InlineData("M0NPT", CwWorkedStatus.Worked)]
    [InlineData("m0npt", CwWorkedStatus.Worked)]
    [InlineData("M0NPT/P", CwWorkedStatus.Worked)]
    [InlineData("D0NPT", CwWorkedStatus.New)]
    [InlineData("EA8/G4ABC", CwWorkedStatus.New)]
    public void CallsAreCheckedAgainstTheLogbook(string call, CwWorkedStatus expected)
    {
        Assert.Equal(expected, CwWorkedLookup.ForCall(call, Calls));
    }

    [Theory]
    [InlineData("IO92KX", CwWorkedStatus.Worked)]
    [InlineData("IO92", CwWorkedStatus.Worked)]
    [InlineData("io91", CwWorkedStatus.Worked)]
    [InlineData("JO01", CwWorkedStatus.New)]
    public void LocatorsAreCheckedByField(string locator, CwWorkedStatus expected)
    {
        Assert.Equal(expected, CwWorkedLookup.ForLocator(locator, Fields));
    }

    [Fact]
    public void BeforeTheLogbookIsReadNothingIsMarked()
    {
        Assert.Equal(CwWorkedStatus.Unknown, CwWorkedLookup.ForCall("M0NPT", null));
        Assert.Equal(CwWorkedStatus.Unknown, CwWorkedLookup.ForLocator("IO92", null));
    }
}
