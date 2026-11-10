using System.Buffers;
using System.Text.Json;
using JSONConverterPro.Core.Models;

namespace JSONConverterPro.Core.Services;

internal enum StepResult { NeedMoreData, Done }
internal delegate StepResult ReaderStep(ref Utf8JsonReader reader);

/// <summary>
/// Drives a Utf8JsonReader over a Stream using a bounded, growable buffer so that large files are
/// processed incrementally instead of being loaded into memory.
/// </summary>
internal static class Utf8JsonPump
{
    public static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 512
    };

    public static void Run(Stream stream, ReaderStep step, CancellationToken ct, Action<long>? onBytes = null)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        int length = 0;
        bool isFinal = false, bomChecked = false;
        long total = 0;
        var state = new JsonReaderState(ReaderOptions);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                while (!isFinal && length < buffer.Length)
                {
                    int n = stream.Read(buffer, length, buffer.Length - length);
                    if (n == 0) isFinal = true; else length += n;
                }
                if (!bomChecked && (length >= 3 || isFinal))
                {
                    if (length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                    {
                        Buffer.BlockCopy(buffer, 3, buffer, 0, length - 3);
                        length -= 3; total += 3;
                    }
                    bomChecked = true;
                }

                var reader = new Utf8JsonReader(buffer.AsSpan(0, length), isFinal, state);
                var result = step(ref reader);
                int consumed = (int)reader.BytesConsumed;
                state = reader.CurrentState;
                total += consumed;
                onBytes?.Invoke(total);

                if (result == StepResult.Done || isFinal) return;

                if (consumed > 0)
                {
                    Buffer.BlockCopy(buffer, consumed, buffer, 0, length - consumed);
                    length -= consumed;
                }
                else if (length == buffer.Length)
                {
                    var bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    Buffer.BlockCopy(buffer, 0, bigger, 0, length);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = bigger;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>Streams the records of the chosen data source one at a time.</summary>
public sealed class JsonParserService
{
    public void ForEachRecord(Stream stream, DataSourceCandidate source,
        Func<JsonElement, long, bool> onRecord, CancellationToken ct, Action<long>? onBytes = null)
    {
        var s = new RecordStepper(source, onRecord);
        try
        {
            Utf8JsonPump.Run(stream, s.Step, ct, onBytes);
        }
        catch (JsonException ex)
        {
            throw JsonErrors.Wrap(ex);
        }
    }

    private enum St { Start, InObject, ExpectValue, InRecords, Trailing }

    private sealed class RecordStepper
    {
        private readonly DataSourceCandidate _src;
        private readonly Func<JsonElement, long, bool> _onRecord;
        private St _state = St.Start;
        private int _matched, _objDepth;
        private bool _found;
        private string? _pending;
        private long _index;

        public RecordStepper(DataSourceCandidate src, Func<JsonElement, long, bool> onRecord)
        {
            _src = src; _onRecord = onRecord;
        }

        public StepResult Step(ref Utf8JsonReader reader)
        {
            while (true)
            {
                var before = reader;
                switch (_state)
                {
                    case St.Start:
                    {
                        if (!reader.Read()) return StepResult.NeedMoreData;
                        if (_src.Kind == DataSourceKind.RootObject)
                        {
                            if (!JsonDocument.TryParseValue(ref reader, out var doc)) { reader = before; return StepResult.NeedMoreData; }
                            using (doc)
                            {
                                _index++;
                                if (!_onRecord(doc.RootElement, _index)) return StepResult.Done;
                            }
                            _state = St.Trailing;
                        }
                        else if (_src.Kind == DataSourceKind.RootArray)
                        {
                            if (reader.TokenType != JsonTokenType.StartArray)
                                throw new UserFacingException("The JSON structure no longer matches the selected data array.");
                            _state = St.InRecords;
                        }
                        else
                        {
                            if (reader.TokenType != JsonTokenType.StartObject)
                                throw new UserFacingException("The JSON structure no longer matches the selected data array.");
                            _objDepth = 1; _state = St.InObject;
                        }
                        break;
                    }
                    case St.InObject:
                    {
                        if (!reader.Read()) return StepResult.NeedMoreData;
                        if (reader.TokenType == JsonTokenType.PropertyName)
                        {
                            _pending = reader.GetString();
                            _state = St.ExpectValue;
                        }
                        else if (reader.TokenType == JsonTokenType.EndObject)
                        {
                            _objDepth--;
                            if (_objDepth == 0) _state = St.Trailing;
                            else if (_found) { /* climbing back out after the array */ }
                            else _matched--;
                        }
                        break;
                    }
                    case St.ExpectValue:
                    {
                        if (!reader.Read()) return StepResult.NeedMoreData;
                        bool isTarget = !_found && _matched < _src.PathSegments.Count &&
                                        string.Equals(_pending, _src.PathSegments[_matched], StringComparison.Ordinal);
                        if (isTarget && _matched == _src.PathSegments.Count - 1 && reader.TokenType == JsonTokenType.StartArray)
                        {
                            _found = true; _state = St.InRecords;
                            break;
                        }
                        if (isTarget && _matched < _src.PathSegments.Count - 1 && reader.TokenType == JsonTokenType.StartObject)
                        {
                            _matched++; _objDepth++; _state = St.InObject;
                            break;
                        }
                        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                        {
                            if (!reader.TrySkip()) { reader = before; return StepResult.NeedMoreData; }
                        }
                        _state = St.InObject;
                        break;
                    }
                    case St.InRecords:
                    {
                        if (!reader.Read()) return StepResult.NeedMoreData;
                        if (reader.TokenType == JsonTokenType.EndArray)
                        {
                            _state = _src.Kind == DataSourceKind.RootArray ? St.Trailing : St.InObject;
                            break;
                        }
                        if (!JsonDocument.TryParseValue(ref reader, out var doc)) { reader = before; return StepResult.NeedMoreData; }
                        using (doc)
                        {
                            _index++;
                            if (!_onRecord(doc.RootElement, _index)) return StepResult.Done;
                        }
                        break;
                    }
                    case St.Trailing:
                    {
                        // Keep reading so trailing garbage is detected (the reader throws on it).
                        if (!reader.Read()) return StepResult.NeedMoreData;
                        break;
                    }
                }
            }
        }
    }
}

internal static class JsonErrors
{
    public static UserFacingException Wrap(JsonException ex)
    {
        string where = ex.LineNumber.HasValue ? $" near line {ex.LineNumber + 1}, position {(ex.BytePositionInLine ?? 0) + 1}" : "";
        return new UserFacingException($"This file is not valid JSON{where}. Check for missing commas, quotes or brackets.", ex);
    }
}
