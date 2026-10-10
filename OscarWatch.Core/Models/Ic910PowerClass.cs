namespace OscarWatch.Core.Models;

/// <summary>
/// PA rating for an IC-910. CI-V cannot tell these apart, so the operator chooses.
/// <see cref="H"/> is the default because older settings stored only <c>IcomIc910</c>
/// and OscarWatch treated that as the high-power set.
/// </summary>
public enum Ic910PowerClass
{
    /// <summary>Export high-power set: 100 W on 144 MHz, 75 W on 430 MHz.</summary>
    H,

    /// <summary>Japanese 50 W set: 50 W on 144 MHz and 430 MHz.</summary>
    D,

    /// <summary>Japanese 20 W set: 20 W on 144 MHz and 430 MHz.</summary>
    Base
}
