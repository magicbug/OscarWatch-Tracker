using OscarWatch.Ft4.Core.Native;

namespace OscarWatch.Ft4.Core.Coding;

public static class Ft4CoderFactory
{
    public static IFt4Coder CreatePreferNative()
    {
        if (NativeFt4Loader.IsAvailable)
            return new Ft4NativeCoder();
        return new FakeFt4Coder();
    }

    public static IFt4Coder CreateNativeOrThrow()
    {
        if (!NativeFt4Loader.IsAvailable)
            throw new InvalidOperationException(NativeFt4Loader.LoadError ?? "ft4_coder is not available on this platform.");
        return new Ft4NativeCoder();
    }
}
