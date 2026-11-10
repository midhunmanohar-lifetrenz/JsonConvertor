namespace JSONConverterPro.Core.Services;

/// <summary>
/// Presents newline-delimited JSON (one value per line) as a single JSON array, on the fly, so the normal
/// streaming parser can read it. Line breaks are preserved so error line numbers match the original text.
/// </summary>
internal sealed class JsonLinesStream : Stream
{
    private readonly Stream _in;
    private readonly byte[] _buf = new byte[64 * 1024];
    private int _pos, _len;
    private bool _started, _eof, _ended, _pending, _bomChecked;
    private byte _carry;

    public JsonLinesStream(Stream inner) { _in = inner; }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> dest)
    {
        int w = 0;
        if (dest.Length == 0) return 0;
        if (!_started) { dest[w++] = (byte)'['; _started = true; }
        while (w < dest.Length)
        {
            if (_carry != 0) { dest[w++] = _carry; _carry = 0; continue; }
            if (_pos == _len)
            {
                if (_eof)
                {
                    if (!_ended) { dest[w++] = (byte)']'; _ended = true; }
                    break;
                }
                _len = _in.Read(_buf, 0, _buf.Length);
                _pos = 0;
                if (_len == 0) { _eof = true; continue; }
                if (!_bomChecked)
                {
                    _bomChecked = true;
                    if (_len >= 3 && _buf[0] == 0xEF && _buf[1] == 0xBB && _buf[2] == 0xBF) _pos = 3;
                }
                continue;
            }
            byte b = _buf[_pos++];
            if (b == (byte)'\n')
            {
                if (_pending) { dest[w++] = (byte)','; _carry = (byte)'\n'; _pending = false; }   // separator at the end of the line
                else dest[w++] = b;
            }
            else
            {
                dest[w++] = b;
                if (b != (byte)' ' && b != (byte)'\t' && b != (byte)'\r') _pending = true;
            }
        }
        return w;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _in.Dispose(); base.Dispose(disposing); }
}
