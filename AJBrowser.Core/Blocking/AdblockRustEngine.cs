using System.Buffers;
using System.Text;
using System.Text.Json;

namespace AJBrowser.Core.Blocking;

/// <summary>
/// <see cref="IFilterEngine"/> backed by Brave's adblock-rust (MPL-2.0) through <c>adblock_shim.dll</c>.
/// <para>Build one from list texts with <see cref="Build"/> (slow: seconds for EasyList-sized input, so call it off the
/// UI thread), or load a saved one with <see cref="TryDeserialize"/> (fast). <see cref="Check"/> is the hot path:
/// it runs for every request on the UI thread, so it avoids allocations except for rare redirect answers.</para>
/// </summary>
public sealed unsafe class AdblockRustEngine : IFilterEngine
{
    private static readonly byte[][] KindNames =
        Enum.GetValues<RequestKind>().Select(k => Encoding.UTF8.GetBytes(RequestKinds.EngineName(k))).ToArray();

    private static readonly Lazy<string?> LoadProblem = new(ProbeLibrary);

    private const int StackLimit = 2048;

    private readonly EngineHandle _handle;

    private AdblockRustEngine(EngineHandle handle) => _handle = handle;

    /// <summary>Null when the native engine is usable; otherwise a sentence saying why not (DLL missing, wrong version…).</summary>
    public static string? UnavailableReason => LoadProblem.Value;

    /// <summary>Compiles filter lists (and optional scriptlet/redirect resources) into a new engine.</summary>
    /// <param name="resourcesJson">adblock-rust resources (brave/adblock-resources <c>dist/resources.json</c>), or null.</param>
    /// <exception cref="FilterEngineUnavailableException">The DLL can't be used or the engine couldn't be built.</exception>
    public static AdblockRustEngine Build(IEnumerable<FilterListText> lists, string? resourcesJson = null)
    {
        ArgumentNullException.ThrowIfNull(lists);
        ThrowIfUnavailable();

        var set = NativeMethods.adb_filter_set_new();
        if (set == 0)
        {
            throw new FilterEngineUnavailableException("The ad-block engine couldn't start.");
        }

        try
        {
            foreach (var list in lists)
            {
                var bytes = Encoding.UTF8.GetBytes(list.Text);
                fixed (byte* p = bytes)
                {
                    var rc = NativeMethods.adb_filter_set_add_list(
                        set, p, (nuint)bytes.Length, list.ScriptletPermissions, list.HostsFormat ? (byte)1 : (byte)0);
                    if (rc != NativeMethods.Ok)
                    {
                        throw new FilterEngineUnavailableException($"A filter list couldn't be loaded (code {rc}).");
                    }
                }
            }
        }
        catch
        {
            NativeMethods.adb_filter_set_free(set);
            throw;
        }

        // adb_engine_build consumes the set whether or not it succeeds.
        var engine = NativeMethods.adb_engine_build(set);
        if (engine == 0)
        {
            throw new FilterEngineUnavailableException("The ad-block engine couldn't compile the filter lists.");
        }

        return WithResources(new EngineHandle(engine), resourcesJson);
    }

    /// <summary>Loads an engine saved with <see cref="Serialize"/>; null when the bytes are corrupt, truncated or
    /// from another engine version (rebuild from the lists then).</summary>
    /// <exception cref="FilterEngineUnavailableException">The DLL can't be used.</exception>
    public static AdblockRustEngine? TryDeserialize(ReadOnlySpan<byte> data, string? resourcesJson = null)
    {
        ThrowIfUnavailable();
        if (data.IsEmpty)
        {
            return null;
        }

        nint engine;
        fixed (byte* p = data)
        {
            engine = NativeMethods.adb_engine_deserialize(p, (nuint)data.Length);
        }

        return engine == 0 ? null : WithResources(new EngineHandle(engine), resourcesJson);
    }

    public FilterDecision Check(string url, string sourceUrl, RequestKind kind)
    {
        // Only http(s) and ws(s) can match; skipping the rest also avoids encoding huge data: URLs.
        if (string.IsNullOrEmpty(url) || !(url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                           || url.StartsWith("ws", StringComparison.OrdinalIgnoreCase)))
        {
            return FilterDecision.Allow;
        }

        sourceUrl ??= "";
        var kindName = KindNames[(int)kind < KindNames.Length ? (int)kind : (int)RequestKind.Other];
        var max = Encoding.UTF8.GetMaxByteCount(url.Length + sourceUrl.Length);
        byte[]? rented = null;
        var buffer = max <= StackLimit ? stackalloc byte[max] : (rented = ArrayPool<byte>.Shared.Rent(max));
        try
        {
            var urlLen = Encoding.UTF8.GetBytes(url, buffer);
            var sourceLen = Encoding.UTF8.GetBytes(sourceUrl, buffer[urlLen..]);
            NativeMethods.AdbCheckResult result;
            int rc;
            fixed (byte* p = buffer)
            fixed (byte* k = kindName)
            {
                rc = NativeMethods.adb_engine_check(
                    _handle, p, (nuint)urlLen, p + urlLen, (nuint)sourceLen, k, (nuint)kindName.Length, &result);
            }

            if (rc != NativeMethods.Ok)
            {
                return FilterDecision.Allow;
            }

            return new FilterDecision(
                result.ShouldBlock != 0,
                result.Matched != 0,
                result.Exception != 0,
                result.Important != 0,
                NativeMethods.TakeString(result.Redirect),
                NativeMethods.TakeString(result.RewrittenUrl));
        }
        catch (ObjectDisposedException)
        {
            return FilterDecision.Allow;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    public CosmeticResources GetCosmeticResources(string pageUrl)
    {
        if (string.IsNullOrEmpty(pageUrl) || !pageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return CosmeticResources.Empty;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(pageUrl);
            fixed (byte* p = bytes)
            {
                return CosmeticResources.Parse(
                    NativeMethods.TakeBytes(NativeMethods.adb_engine_url_cosmetic_resources(_handle, p, (nuint)bytes.Length)));
            }
        }
        catch (ObjectDisposedException)
        {
            return CosmeticResources.Empty;
        }
    }

    public IReadOnlyList<string> GetHiddenClassIdSelectors(
        IEnumerable<string> classes,
        IEnumerable<string> ids,
        IEnumerable<string> exceptions)
    {
        var query = JsonSerializer.SerializeToUtf8Bytes(new
        {
            classes = classes ?? [],
            ids = ids ?? [],
            exceptions = exceptions ?? [],
        });

        try
        {
            byte[] json;
            fixed (byte* p = query)
            {
                json = NativeMethods.TakeBytes(NativeMethods.adb_engine_hidden_class_id_selectors(_handle, p, (nuint)query.Length));
            }

            return json.Length == 0 ? [] : JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (Exception ex) when (ex is ObjectDisposedException or JsonException)
        {
            return [];
        }
    }

    public byte[] Serialize() => NativeMethods.TakeBytes(NativeMethods.adb_engine_serialize(_handle));

    public void Dispose() => _handle.Dispose();

    /// <summary>True when scriptlet/redirect resources were given and accepted. False without them: network blocking
    /// and hiding still work, but <c>##+js(...)</c> rules (what removes YouTube ads) do nothing.</summary>
    public bool HasResources { get; private init; }

    private static AdblockRustEngine WithResources(EngineHandle handle, string? resourcesJson)
    {
        var accepted = false;
        if (!string.IsNullOrEmpty(resourcesJson))
        {
            var bytes = Encoding.UTF8.GetBytes(resourcesJson);
            fixed (byte* p = bytes)
            {
                // A rejected file keeps the engine usable (blocking without scriptlets) rather than failing the build.
                accepted = NativeMethods.adb_engine_use_resources(handle, p, (nuint)bytes.Length) == NativeMethods.Ok;
            }
        }

        return new AdblockRustEngine(handle) { HasResources = accepted };
    }

    private static void ThrowIfUnavailable()
    {
        if (UnavailableReason is { } problem)
        {
            throw new FilterEngineUnavailableException(problem);
        }
    }

    private static string? ProbeLibrary()
    {
        try
        {
            var version = NativeMethods.adb_abi_version();
            return version == NativeMethods.ExpectedAbiVersion
                ? null
                : $"adblock_shim.dll has interface version {version}, AJBrowser expects {NativeMethods.ExpectedAbiVersion}.";
        }
        catch (DllNotFoundException)
        {
            return "adblock_shim.dll is missing next to AJBrowser.";
        }
        catch (EntryPointNotFoundException)
        {
            return "adblock_shim.dll is too old for this AJBrowser.";
        }
        catch (BadImageFormatException)
        {
            return "adblock_shim.dll is damaged or built for another processor.";
        }
    }
}
