namespace OscarWatch.Ft4.Core.Models;

public sealed class Ft4Settings
{
    public string InputDeviceId { get; set; } = "";
    public string OutputDeviceId { get; set; } = "";
    public string Callsign { get; set; } = "";
    public string GridSquare { get; set; } = "";
    public int RxAudioFrequencyHz { get; set; } = 1500;
    public int TxAudioFrequencyHz { get; set; } = 1500;
    public int CutoffFrequencyHz { get; set; } = 4000;
    public bool TxOdd { get; set; } = true;
    public float TxGain { get; set; } = 1.0f;
}
