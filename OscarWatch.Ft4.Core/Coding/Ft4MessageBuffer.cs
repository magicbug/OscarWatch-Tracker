using System.Text;
using OscarWatch.Ft4.Core.Native;

namespace OscarWatch.Ft4.Core.Coding;

public static class Ft4MessageBuffer
{
    public static byte[] FormatMessage(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var buffer = new byte[Ft4Constants.EncodeMessageLength];
        Array.Fill(buffer, (byte)' ');

        var bytes = Encoding.ASCII.GetBytes(text);
        Array.Copy(bytes, 0, buffer, 0, Math.Min(bytes.Length, buffer.Length));
        return buffer;
    }

    public static byte[] FormatCall(string call)
    {
        ArgumentNullException.ThrowIfNull(call);

        var buffer = new byte[Ft4Constants.MaxCallLength];
        Array.Fill(buffer, (byte)' ');

        var bytes = Encoding.ASCII.GetBytes(call.Trim());
        Array.Copy(bytes, 0, buffer, 0, Math.Min(bytes.Length, buffer.Length));
        return buffer;
    }
}
