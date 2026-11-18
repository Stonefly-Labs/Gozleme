namespace Gozleme.Api.Manifests.Parsing;

/// <summary>
/// Maps offsets to 1-based line and column in Unicode scalar values (contracts/manifest-validation.md). LF, CR and
/// CRLF each end a line. A cursor only moves forward, so positioning every node of a document is linear.
/// </summary>
internal sealed class Utf8PositionCursor(ReadOnlyMemory<byte> text, int start = 0)
{
    private int _offset = start;
    private int _line = 1;
    private int _column = 1;

    /// <summary>The position of byte offset <paramref name="offset"/>; offsets must not decrease between calls.</summary>
    public (int Line, int Column) At(long offset)
    {
        var span = text.Span;
        var target = (int)Math.Min(offset, span.Length);
        for (; _offset < target; _offset++)
        {
            var b = span[_offset];
            if (b == (byte)'\n')
            {
                if (_offset == 0 || span[_offset - 1] != (byte)'\r')
                {
                    _line++;
                }

                _column = 1;
            }
            else if (b == (byte)'\r')
            {
                _line++;
                _column = 1;
            }
            else if ((b & 0xC0) != 0x80)
            {
                _column++;
            }
        }

        return (_line, _column);
    }
}

/// <summary>The string form of <see cref="Utf8PositionCursor"/> over UTF-16 indexes; moving backwards restarts it.</summary>
internal sealed class StringPositionCursor(string text)
{
    private int _index;
    private int _line = 1;
    private int _column = 1;

    public (int Line, int Column) At(int index)
    {
        if (index < _index)
        {
            (_index, _line, _column) = (0, 1, 1);
        }

        var end = Math.Min(index, text.Length);
        for (; _index < end; _index++)
        {
            var c = text[_index];
            if (c == '\n')
            {
                if (_index == 0 || text[_index - 1] != '\r')
                {
                    _line++;
                }

                _column = 1;
            }
            else if (c == '\r')
            {
                _line++;
                _column = 1;
            }
            else if (!char.IsLowSurrogate(c))
            {
                _column++;
            }
        }

        return (_line, _column);
    }
}

internal static class TextPositions
{
    /// <summary>The position of UTF-16 index <paramref name="index"/> in <paramref name="text"/> (a surrogate pair is one column).</summary>
    public static (int Line, int Column) Of(string text, int index) => new StringPositionCursor(text).At(index);

    /// <summary>The byte offset of a 0-based (LF-counted line, byte in line) pair as <c>Utf8JsonReader</c> reports it.</summary>
    public static long Utf8Offset(ReadOnlySpan<byte> text, long lfLine, long byteInLine)
    {
        long offset = 0;
        for (long line = 0; line < lfLine && offset < text.Length; offset++)
        {
            if (text[(int)offset] == (byte)'\n')
            {
                line++;
            }
        }

        return Math.Min(offset + byteInLine, text.Length);
    }
}
