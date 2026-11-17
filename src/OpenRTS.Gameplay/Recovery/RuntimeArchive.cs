using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Frent;

namespace OpenRTS.Core;

// Closed, versioned value layouts. No CLR type names, native handles, delegates or object activation from input.
// Reflection is confined to explicitly named fields and runs only when taking/loading a snapshot.
internal sealed class RuntimeArchive
{
    private readonly Dictionary<Type, FieldInfo[]> _layouts = [];
    internal readonly BinaryReader? Reader;
    internal readonly BinaryWriter? Writer;
    private readonly CancellationToken _cancellation;
    private long _remainingItems = 16 * 1024 * 1024;
    internal CancellationToken Cancellation => _cancellation;
    private long _allocationBudget = 256L * 1024 * 1024;
    private void Charge(long bytes) { if ((_allocationBudget -= bytes) < 0) throw new InvalidDataException("Snapshot allocation budget exceeded."); }
    internal bool Reading => Reader is not null;
    internal RuntimeArchive(BinaryReader? reader, BinaryWriter? writer, CancellationToken cancellation)
    { Reader = reader; Writer = writer; _cancellation = cancellation; }

    internal void Layout<T>(params string[] names) => LayoutOmitting<T>([], names);
    internal void LayoutOmitting<T>(string[] omitted, params string[] names)
    {
        Type type = typeof(T); if (_layouts.ContainsKey(type)) return;
        var fields = names.Select(n => Field(type, n)).ToArray();
        var excluded = omitted.Select(n => Field(type, n)).ToHashSet();
        if (type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Any(f => !fields.Contains(f) && !excluded.Contains(f)))
            throw new InvalidOperationException($"Runtime snapshot schema must account for every {type.Name} field.");
        _layouts[type] = fields;
    }
    private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? type.GetField($"<{name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Missing snapshot field {type.Name}.{name}.");

    internal T Value<T>(T value = default!) => (T)Value(typeof(T), value, 0)!;
    internal void Fields(object target, params string[] names)
    {
        foreach (string name in names)
        {
            var f = Field(target.GetType(), name);
            object? result = Value(f.FieldType, f.GetValue(target), 0);
            if (Reading) f.SetValue(target, result);
        }
    }
    internal void Component<T>(Entity entity)
    {
        bool exists = Value(entity.Has<T>());
        if (Reading)
        {
            if (entity.Has<T>()) entity.Remove<T>();
            if (exists) entity.Add(Value<T>());
        }
        else if (exists) Value(entity.Get<T>());
    }
    internal void Custom(object? mechanic, Entity? entity = null)
    {
        if (mechanic is not IRuntimeSnapshotMechanic snapshot) return;
        byte[] data;
        if (Reading)
        {
            int length = Count(0, 1024 * 1024); data = Reader!.ReadBytes(length);
            if (data.Length != length) throw new EndOfStreamException();
            using var stream = new MemoryStream(data, false); using var reader = new BinaryReader(stream);
            if (entity is { } actor) snapshot.ReadEntitySnapshot(reader, actor); else snapshot.ReadSnapshot(reader);
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing mechanic snapshot state.");
        }
        else
        {
            using var stream = new SnapshotBuffer(1024 * 1024); using var writer = new BinaryWriter(stream);
            if (entity is { } actor) snapshot.WriteEntitySnapshot(writer, actor); else snapshot.WriteSnapshot(writer);
            data = stream.ToArray(); Count(data.Length, 1024 * 1024); Writer!.Write(data);
        }
    }
    internal int Count(int count, int maximum = 1000000)
    {
        int n = Value(count);
        _cancellation.ThrowIfCancellationRequested();
        if (n < 0 || n > maximum || (_remainingItems -= n) < 0) throw new InvalidDataException("Snapshot collection budget exceeded.");
        if (Reading && n > Reader!.BaseStream.Length - Reader.BaseStream.Position) throw new InvalidDataException("Truncated snapshot collection.");
        return n;
    }
    private object? Value(Type type, object? value, int depth)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (depth > 32) throw new InvalidDataException("Snapshot nesting budget exceeded.");
        if (type == typeof(bool)) { if (Reading) { byte b = Reader!.ReadByte(); if (b > 1) throw new InvalidDataException("Invalid boolean."); return b != 0; } Writer!.Write((bool)value!); return value; }
        if (type == typeof(byte)) { if (Reading) return Reader!.ReadByte(); Writer!.Write((byte)value!); return value; }
        if (type == typeof(int)) { if (Reading) return Reader!.ReadInt32(); Writer!.Write((int)value!); return value; }
        if (type == typeof(uint)) { if (Reading) return Reader!.ReadUInt32(); Writer!.Write((uint)value!); return value; }
        if (type == typeof(long)) { if (Reading) return Reader!.ReadInt64(); Writer!.Write((long)value!); return value; }
        if (type == typeof(ulong)) { if (Reading) return Reader!.ReadUInt64(); Writer!.Write((ulong)value!); return value; }
        if (type.IsEnum)
        {
            int number = (int)Value(typeof(int), Reading ? 0 : Convert.ToInt32(value), depth)!;
            return Enum.ToObject(type, number);
        }
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return (bool)Value(typeof(bool), value is not null, depth)! ? Value(underlying, value, depth + 1) : null;
        if (!type.IsValueType && !(bool)Value(typeof(bool), value is not null, depth)!) return null;
        if (type == typeof(string))
        {
            byte[] bytes = Reading ? [] : Encoding.UTF8.GetBytes((string)value!);
            int n = Count(bytes.Length, 4096);
            if (Reading) { bytes = Reader!.ReadBytes(n); if (bytes.Length != n) throw new EndOfStreamException(); return new UTF8Encoding(false, true).GetString(bytes); }
            Writer!.Write(bytes); return value;
        }
        if (type.IsArray)
        {
            Type element = type.GetElementType()!;
            var array = (Array?)value;
            int n = Count(array?.Length ?? 0, 16 * 1024 * 1024);
            if (Reading) { Charge((long)n * (element == typeof(byte) || element == typeof(bool) ? 1 : element == typeof(int) || element == typeof(uint) ? 4 : element == typeof(long) || element == typeof(ulong) ? 8 : 128)); array = Array.CreateInstance(element, n); }
            for (int i = 0; i < n; i++)
            {
                var result = Value(element, Reading ? null : array!.GetValue(i), depth + 1);
                if (Reading) array!.SetValue(result, i);
            }
            return array;
        }
        if (type.IsGenericType)
        {
            var generic = type.GetGenericTypeDefinition(); var args = type.GetGenericArguments();
            if (generic == typeof(Dictionary<,>) || generic == typeof(SortedDictionary<,>))
            {
                var dictionary = (IDictionary?)value;
                int n = Count(dictionary?.Count ?? 0);
                if (Reading) { Charge((long)n * 128); dictionary = (IDictionary)Activator.CreateInstance(type)!; }
                // Canonical key encoding avoids depending on Dictionary enumeration or current culture.
                var entries = Reading ? [] : Entries(dictionary!).Select(e => (Entry: e, Key: KeyBytes(args[0], e.Key)))
                    .OrderBy(e => e.Key, ByteComparer.Instance).Select(e => e.Entry).ToArray();
                for (int i = 0; i < n; i++)
                {
                    object key = Value(args[0], Reading ? null : entries[i].Key, depth + 1)!;
                    object? item = Value(args[1], Reading ? null : entries[i].Value, depth + 1);
                    if (Reading) { if (dictionary!.Contains(key)) throw new InvalidDataException("Duplicate snapshot key."); dictionary.Add(key, item); }
                }
                return dictionary;
            }
            if (generic == typeof(List<>) || generic == typeof(Queue<>) || generic == typeof(HashSet<>) || generic == typeof(SortedSet<>))
            {
                object[] items = Reading ? [] : ((IEnumerable)value!).Cast<object>().ToArray();
                if (!Reading && (generic == typeof(HashSet<>) || generic == typeof(SortedSet<>)))
                    items = items.OrderBy(e => KeyBytes(args[0], e), ByteComparer.Instance).ToArray();
                int n = Count(items.Length);
                if (Reading) { Charge((long)n * 128); value = Activator.CreateInstance(type)!; }
                var add = type.GetMethod(generic == typeof(Queue<>) ? "Enqueue" : "Add", [args[0]])!;
                for (int i = 0; i < n; i++)
                {
                    object? item = Value(args[0], Reading ? null : items[i], depth + 1);
                    if (Reading && add.Invoke(value, [item]) is false)
                        throw new InvalidDataException("Duplicate snapshot set entry.");
                }
                return value;
            }
            if (generic == typeof(ValueTuple<,>))
            {
                if (Reading) value = Activator.CreateInstance(type);
                foreach (string name in new[] { "Item1", "Item2" })
                { var f = Field(type, name); var item = Value(f.FieldType, Reading ? null : f.GetValue(value), depth + 1); if (Reading) f.SetValue(value, item); }
                return value;
            }
        }
        if (!_layouts.TryGetValue(type, out var fields)) throw new NotSupportedException($"No runtime snapshot schema for {type.Name}.");
        if (Reading) Charge(128);
        if (Reading) value = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) is { } constructor ? constructor.Invoke(null) : RuntimeHelpers.GetUninitializedObject(type);
        foreach (var field in fields)
        {
            var item = Value(field.FieldType, Reading ? null : field.GetValue(value), depth + 1);
            if (Reading) field.SetValue(value, item);
        }
        return value;
    }
    private static IEnumerable<DictionaryEntry> Entries(IDictionary dictionary)
    { foreach (DictionaryEntry entry in dictionary) yield return entry; }
    private byte[] KeyBytes(Type type, object key)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        var archive = new RuntimeArchive(null, writer, _cancellation);
        foreach (var pair in _layouts) archive._layouts.Add(pair.Key, pair.Value);
        archive.Value(type, key, 0); return stream.ToArray();
    }
    private sealed class ByteComparer : IComparer<byte[]>
    {
        internal static readonly ByteComparer Instance = new();
        public int Compare(byte[]? a, byte[]? b) => a.AsSpan().SequenceCompareTo(b);
    }
}
