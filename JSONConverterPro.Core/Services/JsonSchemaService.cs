using System.Text.Json;
using JSONConverterPro.Core.Models;

namespace JSONConverterPro.Core.Services;

/// <summary>Streams a whole file once to validate it and detect where the data records live.</summary>
public sealed class JsonSchemaService
{
    public JsonStructureInfo Analyze(Stream stream, CancellationToken ct, Action<long>? onBytes = null)
    {
        var a = new Analyzer();
        try
        {
            Utf8JsonPump.Run(stream, a.Step, ct, onBytes);
        }
        catch (JsonException ex)
        {
            // A file with only whitespace/comments has no tokens at all: report it as empty, not as broken JSON.
            if (!a.SawAnyToken) return a.Build();
            throw JsonErrors.Wrap(ex);
        }
        return a.Build();
    }

    private enum St { Start, InObject, ExpectValue, InArray, Trailing }
    private enum Root { None, Object, Array, Scalar }

    private sealed class Analyzer
    {
        private St _state = St.Start;
        private Root _root = Root.None;
        private readonly List<string> _path = new();
        private int _objDepth;
        private string? _pending;
        private long _count, _objCount;
        private string[] _arrayPath = Array.Empty<string>();
        private readonly List<DataSourceCandidate> _arrays = new();
        private long _rootArrayCount; private bool _rootArrayHasObjects;
        public bool SawAnyToken => _root != Root.None;

        public StepResult Step(ref Utf8JsonReader reader)
        {
            while (true)
            {
                var before = reader;
                if (!reader.Read()) return StepResult.NeedMoreData;
                switch (_state)
                {
                    case St.Start:
                        if (reader.TokenType == JsonTokenType.StartObject) { _root = Root.Object; _objDepth = 1; _state = St.InObject; }
                        else if (reader.TokenType == JsonTokenType.StartArray) { _root = Root.Array; _arrayPath = Array.Empty<string>(); _count = _objCount = 0; _state = St.InArray; }
                        else { _root = Root.Scalar; _state = St.Trailing; }
                        break;

                    case St.InObject:
                        if (reader.TokenType == JsonTokenType.PropertyName) { _pending = reader.GetString(); _state = St.ExpectValue; }
                        else if (reader.TokenType == JsonTokenType.EndObject)
                        {
                            if (_objDepth == 1) _state = St.Trailing;
                            else { _objDepth--; _path.RemoveAt(_path.Count - 1); }
                        }
                        break;

                    case St.ExpectValue:
                        if (reader.TokenType == JsonTokenType.StartObject)
                        {
                            _path.Add(_pending ?? ""); _objDepth++; _state = St.InObject;
                        }
                        else if (reader.TokenType == JsonTokenType.StartArray)
                        {
                            _arrayPath = _path.Append(_pending ?? "").ToArray();
                            _count = _objCount = 0; _state = St.InArray;
                        }
                        else _state = St.InObject;
                        break;

                    case St.InArray:
                        if (reader.TokenType == JsonTokenType.EndArray)
                        {
                            if (_root == Root.Array && _arrayPath.Length == 0)
                            {
                                _rootArrayCount = _count; _rootArrayHasObjects = _objCount > 0;
                                _state = St.Trailing;
                            }
                            else
                            {
                                _arrays.Add(new DataSourceCandidate(DataSourceKind.ArrayProperty, _arrayPath, _count, _objCount > 0));
                                _state = St.InObject;
                            }
                        }
                        else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                        {
                            if (!reader.TrySkip()) { reader = before; return StepResult.NeedMoreData; }
                            _count++;
                            if (reader.TokenType == JsonTokenType.EndObject) _objCount++;
                        }
                        else _count++;
                        break;

                    case St.Trailing:
                        break;
                }
            }
        }

        public JsonStructureInfo Build()
        {
            switch (_root)
            {
                case Root.None:
                    return new JsonStructureInfo { IsEmpty = true, RootKind = "empty" };
                case Root.Scalar:
                    return new JsonStructureInfo { IsUnsupportedRoot = true, RootKind = "scalar" };
                case Root.Array:
                {
                    var c = new DataSourceCandidate(DataSourceKind.RootArray, Array.Empty<string>(), _rootArrayCount, _rootArrayHasObjects);
                    return new JsonStructureInfo { Candidates = new[] { c }, Suggested = c, RootKind = "array" };
                }
                default:
                {
                    var objectArrays = _arrays.Where(a => a.ContainsObjects).ToList();
                    var rootObj = new DataSourceCandidate(DataSourceKind.RootObject, Array.Empty<string>(), 1, true);
                    if (objectArrays.Count == 0)
                        return new JsonStructureInfo { Candidates = new[] { rootObj }, Suggested = rootObj, RootKind = "object" };
                    var list = objectArrays.Append(rootObj).ToList();
                    var largest = objectArrays.OrderByDescending(a => a.Count).First();
                    return new JsonStructureInfo
                    {
                        Candidates = list,
                        Suggested = largest,
                        RequiresSelection = objectArrays.Count > 1,
                        RootKind = "wrapper"
                    };
                }
            }
        }
    }
}
