using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace PurrNet.Prediction.Benchmarks.Editor
{
    // Offline analysis only. Reflection keeps this optional tool independent of the
    // Memory Profiler package's internal assembly references and player builds.
    public static class HeapSnapshotSummaryReader
    {
        [Serializable] private sealed class SizeBucket { public long objectBytes, count, bytes; }
        [Serializable] private sealed class Row
        {
            public string type;
            public long count, bytes;
            public SizeBucket[] arraySizes;
            [NonSerialized] public Dictionary<long, SizeBucket> sizeCounts;
        }
        [Serializable] private sealed class Report
        {
            public string snapshot;
            public long objects, managedObjectBytes;
            public Row[] types;
            public ArrayOwner[] arrayOwners;
        }

        [Serializable] private sealed class ArrayOwner
        {
            public string type, path;
            public long objectBytes, count, bytes;
        }

        private sealed class Incoming
        {
            public int source, staticType, field, nestedField;
            public long arrayIndex;
        }

        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static object Get(object target, string name)
        {
            var type = target.GetType();
            var field = type.GetField(name, Members);
            if (field != null) return field.GetValue(target);
            var property = type.GetProperty(name, Members);
            if (property != null) return property.GetValue(target);
            throw new MissingMemberException(type.FullName, name);
        }

        private static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, name);
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing " + name);
            return Path.GetFullPath(args[index + 1]);
        }

        public static void RunFromCommandLine()
        {
            IDisposable reader = null, snapshot = null;
            int exitCode = 0;
            try
            {
                string input = Arg("-heapSnapshot");
                string output = Arg("-heapSummary");
                var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Unity.MemoryProfiler.Editor");
                var readerType = assembly.GetType("Unity.MemoryProfiler.Editor.Format.QueriedSnapshot.FileReader", true);
                reader = (IDisposable)Activator.CreateInstance(readerType, true);
                var error = readerType.GetMethod("Open", Members).Invoke(reader, new object[] { input });
                if (error.ToString() != "Success") throw new InvalidOperationException("Snapshot open: " + error);
                var snapshotType = assembly.GetType("Unity.MemoryProfiler.Editor.CachedSnapshot", true);
                snapshot = (IDisposable)Activator.CreateInstance(snapshotType, Members, null, new object[] { reader }, null);
                var process = (IEnumerator)snapshotType.GetMethod("PostProcess", Members).Invoke(snapshot, null);
                while (process.MoveNext()) { }
                if (!(bool)Get(snapshot, "Valid")) throw new InvalidOperationException("Snapshot crawl did not complete.");
                var data = Get(snapshot, "CrawledData");
                var names = (string[])Get(Get(snapshot, "TypeDescriptions"), "TypeDescriptionName");
                var rows = new Dictionary<int, Row>();
                var report = new Report { snapshot = input };
                foreach (var item in (IEnumerable)Get(data, "m_ManagedObjects"))
                {
                    int index = Convert.ToInt32(Get(item, "ITypeDescription"));
                    long size = Convert.ToInt64(Get(item, "Size"));
                    if (index < 0 || index >= names.Length || size <= 0) continue;
                    if (!rows.TryGetValue(index, out var row)) rows.Add(index, row = new Row { type = names[index] });
                    row.count++; row.bytes += size;
                    if (row.type.EndsWith("[]", StringComparison.Ordinal))
                    {
                        row.sizeCounts ??= new Dictionary<long, SizeBucket>();
                        if (!row.sizeCounts.TryGetValue(size, out var bucket))
                            row.sizeCounts.Add(size, bucket = new SizeBucket { objectBytes = size });
                        bucket.count++; bucket.bytes += size;
                    }
                    report.objects++; report.managedObjectBytes += size;
                }
                report.types = rows.Values.OrderByDescending(row => row.bytes).ToArray();
                foreach (var row in report.types)
                    if (row.sizeCounts != null)
                        row.arraySizes = row.sizeCounts.Values.OrderByDescending(bucket => bucket.bytes).ToArray();
                if (Environment.GetCommandLineArgs().Contains("-heapArrayOwners"))
                    report.arrayOwners = ReadArrayOwners(snapshot, data, names);
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                File.WriteAllText(output, JsonUtility.ToJson(report, true));
                Debug.Log($"HEAP_SUMMARY objects={report.objects}, bytes={report.managedObjectBytes}, types={report.types.Length}");
            }
            catch (Exception error) { Debug.LogException(error); exitCode = 1; }
            finally { snapshot?.Dispose(); reader?.Dispose(); }
            EditorApplication.Exit(exitCode);
        }

        private static ArrayOwner[] ReadArrayOwners(object snapshot, object data, string[] names)
        {
            var objects = ((IEnumerable)Get(data, "m_ManagedObjects")).Cast<object>().ToArray();
            var fields = (string[])Get(Get(snapshot, "FieldDescriptions"), "FieldDescriptionName");
            var incoming = new Dictionary<int, List<Incoming>>();
            object connections = Get(data, "Connections");
            var connectionItem = connections.GetType().GetProperty("Item", Members);
            long connectionCount = Convert.ToInt64(Get(connections, "Count"));
            for (long c = 0; c < connectionCount; c++)
            {
                object connection = connectionItem.GetValue(connections, new object[] { c });
                int target = Convert.ToInt32(Get(connection, "ToManagedObjectIndex"));
                if (target < 0) continue;
                if (!incoming.TryGetValue(target, out var list)) incoming.Add(target, list = new List<Incoming>());
                list.Add(new Incoming
                {
                    source = Convert.ToInt32(Get(connection, "FromManagedObjectIndex")),
                    staticType = Convert.ToInt32(Get(connection, "FromManagedType")),
                    field = Convert.ToInt32(Get(connection, "FieldFrom")),
                    nestedField = Convert.ToInt32(Get(connection, "ValueTypeFieldFrom")),
                    arrayIndex = Convert.ToInt64(Get(connection, "ArrayIndexFrom"))
                });
            }

            string ObjectType(int index)
            {
                int type = Convert.ToInt32(Get(objects[index], "ITypeDescription"));
                return type >= 0 && type < names.Length ? names[type] : "[unknown managed type]";
            }
            string FieldName(int index) => index >= 0 && index < fields.Length ? "." + fields[index] : "";
            var totals = new Dictionary<string, ArrayOwner>();
            for (int i = 0; i < objects.Length; i++)
            {
                int type = Convert.ToInt32(Get(objects[i], "ITypeDescription"));
                long size = Convert.ToInt64(Get(objects[i], "Size"));
                if (type < 0 || type >= names.Length || names[type] != "System.Byte[]" || size <= 0) continue;
                var paths = new HashSet<string>();
                var visited = new HashSet<int>();
                void Trace(int index, string path, int depth)
                {
                    if (depth == 0 || !incoming.TryGetValue(index, out var owners)) { paths.Add(path); return; }
                    if (!visited.Add(index)) { paths.Add("[cycle] -> " + path); return; }
                    foreach (var owner in owners)
                    {
                        string field = FieldName(owner.field);
                        if (owner.nestedField != owner.field) field += FieldName(owner.nestedField);
                        if (owner.arrayIndex >= 0) field = "[]" + field;
                        if (owner.source >= 0)
                            Trace(owner.source, ObjectType(owner.source) + field + " -> " + path, depth - 1);
                        else if (owner.staticType >= 0)
                            paths.Add("static " + names[owner.staticType] + field + " -> " + path);
                        else paths.Add("[native/root] -> " + path);
                    }
                    visited.Remove(index);
                }
                Trace(i, names[type], 6);
                foreach (string path in paths)
                {
                    string key = size + ":" + path;
                    if (!totals.TryGetValue(key, out var row))
                        totals.Add(key, row = new ArrayOwner { type = names[type], objectBytes = size, path = path });
                    row.count++; row.bytes += size;
                }
            }
            // A buffer can have multiple owners; these rows describe references and are not additive heap totals.
            return totals.Values.OrderByDescending(row => row.bytes).ToArray();
        }
    }
}
