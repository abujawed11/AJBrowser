using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AJBrowser.Core.Security;

namespace AJBrowser.Core.Blocking;

/// <summary>A list fully downloaded and assembled (includes inlined, <c>!#if</c> blocks resolved).</summary>
/// <param name="Text">What the engine loads.</param>
/// <param name="SourceUrl">The address that worked (the main one or a mirror).</param>
/// <param name="Expires">From the list header; null when it doesn't say.</param>
/// <param name="Title">From the list header.</param>
/// <param name="Parts">How many files it was made of (1 + includes).</param>
public sealed record DownloadedFilterList(string Text, string SourceUrl, TimeSpan? Expires, string? Title, int Parts);

/// <summary>Thrown with a plain sentence when a list couldn't be downloaded from any of its addresses.</summary>
public sealed class FilterListDownloadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Downloads filter lists. Every file (a list or one of its <c>!#include</c> parts) is fetched with a conditional GET
/// against a small per-address cache (<c>raw\</c>), so unchanged files cost a 304 and no transfer.
/// <para>Safety: https only; includes must be plain relative names in the same folder or below on the same server;
/// at most <see cref="MaxParts"/> files, <see cref="MaxFileBytes"/> each; HTML answers (captive portals, error pages)
/// are refused. No cookies are sent (the client's handler should have <c>UseCookies = false</c>).</para>
/// </summary>
public sealed class FilterListDownloader
{
    public const int MaxFileBytes = 16 * 1024 * 1024;
    public const int MaxParts = 40;
    public const int MaxIncludeDepth = 3;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly HttpClient _http;
    private readonly string _cacheFolder;

    /// <param name="http">Shared client (AJBrowser sets a timeout and no cookies).</param>
    /// <param name="cacheFolder">Where raw files and their ETags are kept (Filters\raw).</param>
    public FilterListDownloader(HttpClient http, string cacheFolder)
    {
        _http = http;
        _cacheFolder = cacheFolder;
    }

    /// <summary>Tries each address of <paramref name="list"/> in order and returns the first complete result.</summary>
    /// <exception cref="FilterListDownloadException">No address worked; the message says why (for the last one tried).</exception>
    public async Task<DownloadedFilterList> DownloadAsync(FilterListInfo list, CancellationToken cancellationToken = default)
    {
        Exception? last = null;
        foreach (var url in list.Urls)
        {
            try
            {
                return await DownloadFromAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FilterListDownloadException or HttpRequestException or IOException
                                           || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                last = ex;
            }
        }

        var reason = last switch
        {
            FilterListDownloadException d => d.Message,
            TaskCanceledException => "the server didn't answer in time",
            HttpRequestException h when h.StatusCode is { } code => $"the server answered {(int)code}",
            _ => "it couldn't be reached",
        };
        throw new FilterListDownloadException($"{list.Title} couldn't be downloaded: {reason}.", last);
    }

    private async Task<DownloadedFilterList> DownloadFromAsync(Uri mainUrl, CancellationToken cancellationToken)
    {
        var main = FilterListPreprocessor.Process(await FetchAsync(mainUrl, cancellationToken).ConfigureAwait(false));
        var text = new StringBuilder(main.Text);
        var parts = 1;
        var seen = new HashSet<string>(StringComparer.Ordinal) { mainUrl.AbsoluteUri };
        var queue = new Queue<(Uri Url, int Depth)>(main.Includes.Select(i => (Resolve(mainUrl, i), 1)));

        while (queue.TryDequeue(out var item))
        {
            if (!seen.Add(item.Url.AbsoluteUri))
            {
                continue;
            }

            if (++parts > MaxParts)
            {
                throw new FilterListDownloadException("it includes too many files");
            }

            var part = FilterListPreprocessor.Process(await FetchAsync(item.Url, cancellationToken).ConfigureAwait(false));
            text.Append('\n').Append(part.Text);
            if (item.Depth < MaxIncludeDepth)
            {
                foreach (var include in part.Includes)
                {
                    queue.Enqueue((Resolve(mainUrl, include, item.Url), item.Depth + 1));
                }
            }
        }

        return new DownloadedFilterList(text.ToString(), mainUrl.AbsoluteUri, main.Expires, main.Title, parts);
    }

    /// <summary>Resolves an <c>!#include</c> target relative to the including file; it must stay on the main list's
    /// server, in the main list's folder or below.</summary>
    internal static Uri Resolve(Uri mainUrl, string include, Uri? includingFile = null)
    {
        if (include.Length is 0 or > 200 || include.Contains("..", StringComparison.Ordinal) || include.Contains(':')
            || include.StartsWith('/') || include.Contains('\\') || include.Contains('?') || include.Contains('#')
            || include.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/')))
        {
            throw new FilterListDownloadException($"it includes a file with an unsafe name ({include})");
        }

        var resolved = new Uri(includingFile ?? mainUrl, include);
        var folder = mainUrl.AbsoluteUri[..(mainUrl.AbsoluteUri.LastIndexOf('/') + 1)];
        if (!resolved.AbsoluteUri.StartsWith(folder, StringComparison.Ordinal))
        {
            throw new FilterListDownloadException($"it includes a file outside its folder ({include})");
        }

        return resolved;
    }

    private async Task<string> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            throw new FilterListDownloadException("its address isn't https");
        }

        var (textFile, metaFile) = CachePaths(url);
        var cached = ReadMeta(metaFile);
        var haveCache = cached is not null && File.Exists(textFile);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (haveCache)
        {
            if (cached!.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
            {
                request.Headers.IfNoneMatch.Add(tag);
            }

            if (cached.LastModified is { } lastModified)
            {
                request.Headers.IfModifiedSince = lastModified;
            }
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified && haveCache)
        {
            return await File.ReadAllTextAsync(textFile, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new FilterListDownloadException($"the server answered {(int)response.StatusCode} for {Redactor.Url(url)}");
        }

        if (response.Content.Headers.ContentLength > MaxFileBytes)
        {
            throw new FilterListDownloadException("a file is too large");
        }

        var text = await ReadTextAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (LooksLikeHtml(text))
        {
            throw new FilterListDownloadException("the server sent a web page instead of a list (sign-in page or error?)");
        }

        Directory.CreateDirectory(_cacheFolder);
        await AtomicFile.WriteTextAsync(textFile, text, cancellationToken).ConfigureAwait(false);
        var meta = new CacheMeta(url.AbsoluteUri, response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
        await AtomicFile.WriteTextAsync(metaFile, JsonSerializer.Serialize(meta, Json), cancellationToken).ConfigureAwait(false);
        return text;
    }

    private static async Task<string> ReadTextAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxFileBytes)
            {
                throw new FilterListDownloadException("a file is too large");
            }

            buffer.Write(chunk, 0, read);
        }

        // Lists are UTF-8 (with or without BOM); invalid bytes become U+FFFD and those rules simply won't match.
        return new UTF8Encoding(false, false).GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('﻿');
    }

    internal static bool LooksLikeHtml(string text)
    {
        var start = text.AsSpan().TrimStart();
        return start.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
               || start.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
               || start.StartsWith("<head", StringComparison.OrdinalIgnoreCase);
    }

    private (string Text, string Meta) CachePaths(Uri url)
    {
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri)))[..32];
        return (Path.Combine(_cacheFolder, name + ".txt"), Path.Combine(_cacheFolder, name + ".json"));
    }

    private static CacheMeta? ReadMeta(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<CacheMeta>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record CacheMeta(string Url, string? ETag, DateTimeOffset? LastModified);
}

/// <summary>Writes files through a temporary file and a rename, so a crash never leaves half a file.</summary>
internal static class AtomicFile
{
    public static void WriteText(string file, string text)
    {
        var temp = file + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, file, overwrite: true);
    }

    public static async Task WriteTextAsync(string file, string text, CancellationToken cancellationToken = default)
    {
        var temp = file + ".tmp";
        await File.WriteAllTextAsync(temp, text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temp, file, overwrite: true);
    }

    public static async Task WriteBytesAsync(string file, byte[] bytes, CancellationToken cancellationToken = default)
    {
        var temp = file + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(temp, file, overwrite: true);
    }
}
