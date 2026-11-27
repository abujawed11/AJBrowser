using System.Runtime.InteropServices;

namespace AJBrowser.Core.Blocking;

/// <summary>
/// The raw functions exported by <c>adblock_shim.dll</c> (native/adblock-shim/src/lib.rs). Only
/// <see cref="AdblockRustEngine"/> calls these; everything here mirrors the Rust signatures exactly.
/// <para>Strings travel as UTF-8 pointer + length; buffers returned by Rust must go back to
/// <see cref="adb_bytes_free"/>.</para>
/// </summary>
internal static unsafe partial class NativeMethods
{
    private const string Dll = "adblock_shim";

    /// <summary>The C interface version this C# code was written against (Rust's <c>ABI_VERSION</c>).</summary>
    public const uint ExpectedAbiVersion = 1;

    public const int Ok = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct AdbBytes
    {
        public byte* Ptr;
        public nuint Len;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AdbCheckResult
    {
        public byte ShouldBlock;
        public byte Matched;
        public byte Exception;
        public byte Important;
        public AdbBytes Redirect;
        public AdbBytes RewrittenUrl;
    }

    [LibraryImport(Dll)]
    public static partial uint adb_abi_version();

    [LibraryImport(Dll)]
    public static partial void adb_bytes_free(AdbBytes bytes);

    [LibraryImport(Dll)]
    public static partial nint adb_filter_set_new();

    [LibraryImport(Dll)]
    public static partial int adb_filter_set_add_list(nint set, byte* text, nuint len, byte permissions, byte hostsFormat);

    [LibraryImport(Dll)]
    public static partial void adb_filter_set_free(nint set);

    /// <summary>Always consumes <paramref name="set"/>.</summary>
    [LibraryImport(Dll)]
    public static partial nint adb_engine_build(nint set);

    [LibraryImport(Dll)]
    public static partial nint adb_engine_deserialize(byte* data, nuint len);

    [LibraryImport(Dll)]
    public static partial AdbBytes adb_engine_serialize(EngineHandle engine);

    [LibraryImport(Dll)]
    public static partial void adb_engine_free(nint engine);

    [LibraryImport(Dll)]
    public static partial int adb_engine_use_resources(EngineHandle engine, byte* json, nuint len);

    [LibraryImport(Dll)]
    public static partial int adb_engine_check(
        EngineHandle engine,
        byte* url,
        nuint urlLen,
        byte* sourceUrl,
        nuint sourceUrlLen,
        byte* requestType,
        nuint requestTypeLen,
        AdbCheckResult* result);

    [LibraryImport(Dll)]
    public static partial AdbBytes adb_engine_url_cosmetic_resources(EngineHandle engine, byte* url, nuint len);

    [LibraryImport(Dll)]
    public static partial AdbBytes adb_engine_hidden_class_id_selectors(EngineHandle engine, byte* json, nuint len);

    /// <summary>Copies a Rust buffer into managed memory and frees it.</summary>
    public static byte[] TakeBytes(AdbBytes bytes)
    {
        if (bytes.Ptr == null)
        {
            return [];
        }

        try
        {
            return new ReadOnlySpan<byte>(bytes.Ptr, checked((int)bytes.Len)).ToArray();
        }
        finally
        {
            adb_bytes_free(bytes);
        }
    }

    /// <summary>Decodes a Rust UTF-8 buffer and frees it; null when empty.</summary>
    public static string? TakeString(AdbBytes bytes)
    {
        if (bytes.Ptr == null)
        {
            return null;
        }

        try
        {
            return bytes.Len == 0 ? null : System.Text.Encoding.UTF8.GetString(bytes.Ptr, checked((int)bytes.Len));
        }
        finally
        {
            adb_bytes_free(bytes);
        }
    }
}

/// <summary>
/// Owns one native engine. A <see cref="SafeHandle"/> (think of it as a ref-counted pointer) guarantees the engine
/// is freed exactly once, and never while another thread is still inside a call that uses it.
/// </summary>
internal sealed class EngineHandle : SafeHandle
{
    public EngineHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public EngineHandle(nint pointer)
        : this() => SetHandle(pointer);

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        NativeMethods.adb_engine_free(handle);
        return true;
    }
}
