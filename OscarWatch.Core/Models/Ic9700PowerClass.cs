namespace OscarWatch.Core.Models;

/// <summary>
/// PA rating for an IC-9700. CI-V cannot tell these apart, so the operator chooses.
/// <see cref="Export"/> is the default because older settings stored only <c>IcomIc9700</c>
/// and OscarWatch treated that as 100 W on 144 MHz and 75 W on 430 MHz.
/// </summary>
public enum Ic9700PowerClass
{
    /// <summary>Export set: 100 W on 144 MHz, 75 W on 430 MHz, 10 W on 1200 MHz.</summary>
    Export,

    /// <summary>Japanese IC-9700: 50 W on 144 MHz and 430 MHz, 10 W on 1200 MHz.</summary>
    Japan,

    /// <summary>Japanese IC-9700S: 20 W on 144 MHz and 430 MHz, 10 W on 1200 MHz.</summary>
    JapanS
}
