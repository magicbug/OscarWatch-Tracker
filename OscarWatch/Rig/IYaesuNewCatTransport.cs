namespace OscarWatch.Rig;

internal interface IYaesuNewCatTransport : IDisposable
{
    bool IsOpen { get; }
    void Open();
    bool SendCommand(string command, int postDelayMs = 50);
    string? Transact(string command, int postDelayMs = 50);

    /// <summary>Raise or drop DTR. RTS stays with hardware flow control.</summary>
    bool TrySetDtr(bool assert) => false;
}
