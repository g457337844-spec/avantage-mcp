using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace AvantageMcp
{
    // Only the collection headers and index-reference streams are decoded here.
    // Chart persistence belongs to Avantage; the opaque grid body is hashed.
    internal static class ProjectGridValidation
    {
        const int MaxItems = 4096;
        const int MaxReferencePairs = 100000;
        const int MaxStreamBytes = 64 * 1024 * 1024;

        internal static Dictionary<string, object> Capture(string path)
        {
            return Parse(
                ProjectPersistence.ReadStream(path, "Data Spaces", "Contents"),
                ProjectPersistence.ReadStream(path, "Grid Cells", "Contents"),
                ProjectPersistence.ReadStream(path, "Data Spaces", "XRefs"),
                ProjectPersistence.ReadStream(path, "Grid Cells", "XRefs"));
        }

        internal static Dictionary<string, object> Parse(byte[] dataContents,
            byte[] gridContents, byte[] dataReferences, byte[] gridReferences)
        {
            int dataCount = CollectionCount(dataContents);
            int cellCount = CollectionCount(gridContents);
            var fromData = ReferenceGroups(dataReferences, 3);
            var fromCells = ReferenceGroups(gridReferences, 1);

            // The first Data Spaces group and the sole Grid Cells group describe
            // the same edges in opposite directions. Preserve multiplicity and
            // permit a spectrum to appear in several cells.
            var expected = new List<int[]>();
            foreach (var edge in fromData[0])
            {
                CheckIndex(edge[0], dataCount);
                CheckIndex(edge[1], cellCount);
                expected.Add(new[] {edge[1], edge[0]});
            }
            foreach (var edge in fromCells[0])
            {
                CheckIndex(edge[0], cellCount);
                CheckIndex(edge[1], dataCount);
            }
            var actual = Sorted(fromCells[0]);
            var inverse = Sorted(expected);
            if (actual.Length != inverse.Length || actual.Where((edge, index) =>
                    edge[0] != inverse[index][0] || edge[1] != inverse[index][1]).Any())
                throw Invalid("The data list and grid cells have inconsistent reciprocal references.");

            return new Dictionary<string, object> {
                {"spectrum_count", dataCount},
                {"grid_cell_count", cellCount},
                {"grid_data_pairs", actual},
                {"mapped_grid_cell_count", actual.Select(edge => edge[0]).Distinct().Count()},
                {"visible_spectrum_count", actual.Select(edge => edge[1]).Distinct().Count()},
                {"grid_contents_sha256", Hash(gridContents)}
            };
        }

        static int[][] Sorted(IEnumerable<int[]> pairs)
        {
            return pairs.OrderBy(pair => pair[0]).ThenBy(pair => pair[1]).ToArray();
        }

        static int CollectionCount(byte[] contents)
        {
            CheckStream(contents);
            int position = 0;
            int version = ReadInteger(contents, ref position);
            if (version < 0 || version > 1)
                throw Invalid("The processing collection header version is unsupported.");
            int count = ReadInteger(contents, ref position);
            if (count < 0 || count > MaxItems)
                throw Invalid("The processing collection count exceeds the supported limit.");
            return count;
        }

        static List<List<int[]>> ReferenceGroups(byte[] bytes, int groupCount)
        {
            CheckStream(bytes);
            int position = 0;
            if (ReadInteger(bytes, ref position) != 0)
                throw Invalid("The processing reference stream version is unsupported.");
            var groups = new List<List<int[]>>();
            for (int group = 0; group < groupCount; group++)
            {
                int count = ReadInteger(bytes, ref position);
                if (count < 0 || count > MaxReferencePairs ||
                    (long)position + 8L * count + 4 > bytes.Length)
                    throw Invalid("The processing reference group is truncated or exceeds the supported limit.");
                var pairs = new List<int[]>();
                for (int index = 0; index < count; index++)
                    pairs.Add(new[] {ReadInteger(bytes, ref position), ReadInteger(bytes, ref position)});
                if (ReadInteger(bytes, ref position) != 0)
                    throw Invalid("The processing reference group terminator is invalid.");
                groups.Add(pairs);
            }
            if (position != bytes.Length)
                throw Invalid("The processing reference stream contains unexpected trailing data.");
            return groups;
        }

        static void CheckStream(byte[] bytes)
        {
            if (bytes == null || bytes.Length > MaxStreamBytes)
                throw Invalid("The processing metadata stream is missing or exceeds the supported limit.");
        }

        static int ReadInteger(byte[] bytes, ref int position)
        {
            if (position < 0 || (long)position + 4 > bytes.Length)
                throw Invalid("The processing metadata stream is truncated.");
            int value = BitConverter.ToInt32(bytes, position);
            position += 4;
            return value;
        }

        static void CheckIndex(int value, int count)
        {
            if (value < 0 || value >= count)
                throw Invalid("A processing grid reference points outside its collection.");
        }

        static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }

        static Fault Invalid(string message)
        {
            return new Fault("INVALID_PROJECT_GRID", message);
        }
    }
}
