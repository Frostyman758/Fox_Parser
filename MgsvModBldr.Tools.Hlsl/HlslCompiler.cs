using System.Runtime.InteropServices;
using System.Text;

namespace MgsvModBldr.Tools.Hlsl;

/// <summary>
/// HLSL -> DXBC compiler backed by D3DCompile (d3dcompiler_47.dll — the
/// same engine fxc.exe uses, so the output is standard SM5 DXBC that MGSV
/// loads). Windows only; on other platforms it throws a clear message
/// (the decompile/extract side stays cross-platform). The result is
/// functional, NOT byte-identical to Konami's original .fxc — the
/// sanctioned exception to the toolset's byte-exact rule.
/// </summary>
public static class HlslCompiler
{
    public static bool IsAvailable => OperatingSystem.IsWindows();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr GetBufferPointerFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate nuint GetBufferSizeFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseFn(IntPtr self);

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(
        byte[] pSrcData, nuint SrcDataSize,
        [MarshalAs(UnmanagedType.LPStr)] string pSourceName,
        IntPtr pDefines, IntPtr pInclude,
        [MarshalAs(UnmanagedType.LPStr)] string pEntrypoint,
        [MarshalAs(UnmanagedType.LPStr)] string pTarget,
        uint Flags1, uint Flags2,
        out IntPtr ppCode, out IntPtr ppErrorMsgs);

    /// <summary>
    /// Compile preprocessed HLSL bytes to a DXBC (.fxc) blob. Throws on
    /// failure (with the compiler error text). Source bytes are passed
    /// as-is so Shift-JIS comments are tolerated (lexer skips // to EOL).
    /// </summary>
    public static byte[] Compile(byte[] source, string sourceName, string entryPoint, string target, uint flags)
    {
        if (!IsAvailable)
            throw new PlatformNotSupportedException("HLSL recompile needs d3dcompiler (Windows). Decompile/extract is cross-platform; Linux recompile would need libvkd3d-shader.");

        IntPtr code = IntPtr.Zero, err = IntPtr.Zero;
        int hr;
        try
        {
            hr = D3DCompile(source, (nuint)source.Length, sourceName, IntPtr.Zero, IntPtr.Zero,
                            entryPoint, target, flags, 0, out code, out err);
        }
        catch (DllNotFoundException)
        {
            throw new PlatformNotSupportedException("d3dcompiler_47.dll not found — install the Windows SDK / D3DCompiler redistributable.");
        }

        if (hr < 0 || code == IntPtr.Zero)
        {
            string msg = err != IntPtr.Zero ? BlobToString(err) : $"HRESULT 0x{hr:X8}";
            if (err != IntPtr.Zero) Release(err);
            if (code != IntPtr.Zero) Release(code);
            throw new InvalidOperationException("D3DCompile failed: " + msg.Trim());
        }

        var result = BlobToBytes(code);
        Release(code);
        if (err != IntPtr.Zero) Release(err);
        return result;
    }

    private static T Vtbl<T>(IntPtr obj, int index) where T : Delegate
    {
        IntPtr vtbl = Marshal.ReadIntPtr(obj);
        IntPtr fn = Marshal.ReadIntPtr(vtbl, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    private static byte[] BlobToBytes(IntPtr blob)
    {
        var getPtr = Vtbl<GetBufferPointerFn>(blob, 3);
        var getSize = Vtbl<GetBufferSizeFn>(blob, 4);
        IntPtr p = getPtr(blob);
        int n = (int)getSize(blob);
        var bytes = new byte[n];
        Marshal.Copy(p, bytes, 0, n);
        return bytes;
    }

    private static string BlobToString(IntPtr blob)
    {
        var getPtr = Vtbl<GetBufferPointerFn>(blob, 3);
        var getSize = Vtbl<GetBufferSizeFn>(blob, 4);
        IntPtr p = getPtr(blob);
        int n = (int)getSize(blob);
        var bytes = new byte[n];
        Marshal.Copy(p, bytes, 0, n);
        return Encoding.ASCII.GetString(bytes);
    }

    private static void Release(IntPtr obj) => Vtbl<ReleaseFn>(obj, 2)(obj);
}
