namespace MX.MailServer.Smtp;

public sealed class SmtpLineTooLongException : Exception;

/// <summary>A line read from the client, without its terminator.</summary>
public readonly record struct SmtpLine(byte[] Bytes, bool EndedWithCrLf);

/// <summary>
/// Buffered byte-level line reader. Works on raw bytes so 8-bit message content is preserved,
/// and reports whether each line ended with CRLF (bare LF matters for SMTP smuggling defence).
/// </summary>
public sealed class SmtpLineReader
{
    private Stream _stream;
    private readonly byte[] _buffer = new byte[16 * 1024];
    private int _start;
    private int _end;

    public SmtpLineReader(Stream stream) => _stream = stream;

    /// <summary>True when the client sent data beyond the last line read (pipelining).</summary>
    public bool HasBufferedData => _end > _start;

    /// <summary>Switches to a new stream (after STARTTLS) and discards anything buffered from the old one.</summary>
    public void Reset(Stream stream)
    {
        _stream = stream;
        _start = _end = 0;
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes (an IMAP literal). Returns null on disconnect.</summary>
    public async ValueTask<byte[]?> ReadExactAsync(int count, CancellationToken ct)
    {
        var result = new byte[count];
        var filled = 0;
        while (filled < count)
        {
            if (_end > _start)
            {
                var take = Math.Min(count - filled, _end - _start);
                Buffer.BlockCopy(_buffer, _start, result, filled, take);
                _start += take;
                filled += take;
                continue;
            }
            _start = _end = 0;
            var read = await _stream.ReadAsync(_buffer.AsMemory(), ct);
            if (read == 0) return null;
            _end = read;
        }
        return result;
    }

    /// <returns>The line, or null when the client closed the connection.</returns>
    public async ValueTask<SmtpLine?> ReadLineAsync(int maxLength, CancellationToken ct)
    {
        List<byte>? spill = null; // used only when a line spans buffer refills
        var overflow = false;
        while (true)
        {
            var lf = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (lf >= 0)
            {
                var length = lf - _start;
                var crlf = length > 0 ? _buffer[lf - 1] == '\r' : spill is { Count: > 0 } && spill[^1] == '\r';
                byte[] line;
                if (spill is null)
                {
                    line = _buffer.AsSpan(_start, crlf ? length - 1 : length).ToArray();
                }
                else
                {
                    spill.AddRange(_buffer.AsSpan(_start, length));
                    if (crlf) spill.RemoveAt(spill.Count - 1);
                    line = spill.ToArray();
                }
                _start = lf + 1;
                if (overflow || line.Length > maxLength) throw new SmtpLineTooLongException();
                return new SmtpLine(line, crlf);
            }

            // No terminator yet: keep what we have (bounded) and read more.
            if (_end > _start)
            {
                spill ??= [];
                if (spill.Count + (_end - _start) > maxLength + 2) overflow = true; // keep consuming until LF, then fail
                else spill.AddRange(_buffer.AsSpan(_start, _end - _start));
            }
            _start = _end = 0;
            var read = await _stream.ReadAsync(_buffer.AsMemory(), ct);
            if (read == 0) return null;
            _end = read;
        }
    }
}
