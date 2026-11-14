using System.Text;
using System.Text.RegularExpressions;

namespace RsyncShell.Mcp;

internal enum OutputMode
{
    Head,
    Tail,
    HeadTail,
}

internal interface IOutputSink
{
    void Append(ReadOnlySpan<byte> bytes);
}

/// <summary>
/// Retains a bounded part of a byte stream: its start, its end, or both halves.
/// Everything else is counted and dropped, so memory stays bounded while the
/// remote process is always drained to completion.
/// </summary>
internal sealed class OutputCapture : IOutputSink
{
    private readonly int _headLimit;
    private readonly int _tailLimit;
    private readonly MemoryStream _head;
    private byte[]? _tail;
    private int _tailNext;
    private int _tailCount;

    public OutputCapture(int limit, OutputMode mode = OutputMode.Head)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        _tailLimit = mode switch
        {
            OutputMode.Head => 0,
            OutputMode.Tail => limit,
            _ => limit - limit / 2,
        };
        _headLimit = limit - _tailLimit;
        _head = new MemoryStream(Math.Min(_headLimit, 16 * 1024));
    }

    public long TotalBytes { get; private set; }

    public bool Truncated => TotalBytes > _headLimit + _tailLimit;

    public long OmittedBytes => Math.Max(0, TotalBytes - _headLimit - _tailLimit);

    public void Append(ReadOnlySpan<byte> bytes)
    {
        TotalBytes += bytes.Length;
        var toHead = Math.Min(_headLimit - (int)_head.Length, bytes.Length);
        if (toHead > 0)
        {
            _head.Write(bytes[..toHead]);
            bytes = bytes[toHead..];
        }
        if (bytes.IsEmpty || _tailLimit == 0)
            return;

        _tail ??= new byte[_tailLimit];
        if (bytes.Length >= _tailLimit)
        {
            bytes[^_tailLimit..].CopyTo(_tail);
            _tailNext = 0;
            _tailCount = _tailLimit;
            return;
        }

        var first = Math.Min(bytes.Length, _tailLimit - _tailNext);
        bytes[..first].CopyTo(_tail.AsSpan(_tailNext));
        bytes[first..].CopyTo(_tail);
        _tailNext = (_tailNext + bytes.Length) % _tailLimit;
        _tailCount = Math.Min(_tailLimit, _tailCount + bytes.Length);
    }

    /// <summary>Returns every captured byte in stream order. Complete only when <see cref="Truncated"/> is false.</summary>
    public byte[] ToArray()
    {
        var head = HeadBytes();
        var tail = TailBytes();
        var result = new byte[head.Length + tail.Length];
        head.CopyTo(result);
        tail.CopyTo(result.AsSpan(head.Length));
        return result;
    }

    public string GetText()
    {
        if (!Truncated)
            return Encoding.UTF8.GetString(ToArray());

        var head = Encoding.UTF8.GetString(TrimIncompleteEnd(HeadBytes()));
        var tail = Encoding.UTF8.GetString(TrimContinuationStart(TailBytes()));
        var marker = $"[... {OmittedBytes} bytes omitted ...]";
        var text = new StringBuilder(head.Length + tail.Length + marker.Length + 2);
        if (head.Length > 0)
        {
            text.Append(head);
            if (head[^1] != '\n')
                text.Append('\n');
        }
        text.Append(marker);
        if (tail.Length > 0)
            text.Append('\n').Append(tail);
        return text.ToString();
    }

    private ReadOnlySpan<byte> HeadBytes() => _head.GetBuffer().AsSpan(0, (int)_head.Length);

    private byte[] TailBytes()
    {
        if (_tail is null || _tailCount == 0)
            return [];
        var result = new byte[_tailCount];
        var start = (_tailNext - _tailCount + _tailLimit) % _tailLimit;
        var first = Math.Min(_tailCount, _tailLimit - start);
        _tail.AsSpan(start, first).CopyTo(result);
        _tail.AsSpan(0, _tailCount - first).CopyTo(result.AsSpan(first));
        return result;
    }

    /// <summary>Drops a UTF-8 sequence that was cut off at the end of a slice.</summary>
    internal static ReadOnlySpan<byte> TrimIncompleteEnd(ReadOnlySpan<byte> bytes)
    {
        for (var back = 1; back <= Math.Min(4, bytes.Length); back++)
        {
            var value = bytes[^back];
            if ((value & 0xC0) == 0x80)
                continue;
            var needed = value >= 0xF0 ? 4 : value >= 0xE0 ? 3 : value >= 0xC0 ? 2 : 1;
            return needed > back ? bytes[..^back] : bytes;
        }
        return bytes;
    }

    /// <summary>Drops UTF-8 continuation bytes left at the start of a slice.</summary>
    internal static ReadOnlySpan<byte> TrimContinuationStart(ReadOnlySpan<byte> bytes)
    {
        var skip = 0;
        while (skip < Math.Min(3, bytes.Length) && (bytes[skip] & 0xC0) == 0x80)
            skip++;
        return bytes[skip..];
    }
}

/// <summary>Forwards only the lines that match a pattern, like a streaming grep.</summary>
internal sealed class LineFilterSink(Regex pattern, IOutputSink inner) : IOutputSink
{
    private const int MaximumLineBytes = 64 * 1024;
    private readonly MemoryStream _line = new();

    public void Append(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            // An overlong line is matched in pieces so memory stays bounded.
            var room = MaximumLineBytes - (int)_line.Length;
            var window = bytes[..Math.Min(room, bytes.Length)];
            var newline = window.IndexOf((byte)'\n');
            var take = newline < 0 ? window.Length : newline + 1;
            _line.Write(bytes[..take]);
            bytes = bytes[take..];
            if (newline >= 0 || _line.Length >= MaximumLineBytes)
                Flush();
        }
    }

    public void Complete()
    {
        if (_line.Length > 0)
            Flush();
    }

    private void Flush()
    {
        var line = _line.GetBuffer().AsSpan(0, (int)_line.Length);
        var text = Encoding.UTF8.GetString(line).TrimEnd('\n', '\r');
        bool matched;
        try
        {
            matched = pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            matched = false;
        }
        if (matched)
            inner.Append(line);
        _line.SetLength(0);
    }

    public static Regex CreatePattern(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        }
        catch (NotSupportedException)
        {
            // Lookarounds and backreferences need the backtracking engine; bound it instead.
            return new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
    }
}
