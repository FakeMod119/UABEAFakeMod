using AssetsTools.NET;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UABEAvalonia;

/// <summary>
/// Bounded import of known Unity serialized RAW layouts. Never sends arbitrary
/// RAW bytes through a historical TypeTree search with unbounded allocations.
/// Material is parsed structurally and reserialized to the observed TARGET wire
/// shape; MonoBehaviour UMA meshes have only a limited legacy RAW fallback.
/// </summary>
internal static class UABEvolRawKnownAssetImport
{
    private const int MaxMaterialBytes = 8 * 1024 * 1024;
    private const int PPtrSize = 12;
    private const int MonoScriptPointerOffset = 16;

    private sealed class MaterialLayout
    {
        public string Name = string.Empty;
        public int ShaderPointer;
        public int KeywordsStart;
        public int KeywordsEnd;
        public bool KeywordsAsArrays;
        public List<string> Keywords = new();
        public List<string> ValidKeywords = new();
        public List<string> InvalidKeywords = new();
        public int IntsStart;
        public int IntsEnd;
        public int IntCount;
        public bool HasInts;
    }

    public static bool TryPrepare(
        AssetContainer destination, byte[] source, out byte[]? prepared, out string diagnostic)
    {
        prepared = null;
        diagnostic = string.Empty;
        bool bigEndian = destination.FileInstance.file.Header.Endianness;
        bool isMaterial = destination.ClassId == (int)AssetClassID.Material;
        bool isMono = destination.ClassId == (int)AssetClassID.MonoBehaviour || destination.ClassId < 0;

        if (isMaterial)
        {
            if (source.Length > MaxMaterialBytes || destination.Size > MaxMaterialBytes)
                throw new InvalidDataException(
                    "Material RAW is too large for bounded structural conversion; destination unchanged.");
            byte[] original = ReadOriginalBytes(destination, MaxMaterialBytes);
            if (!TryParseMaterial(source, bigEndian, out MaterialLayout donor) ||
                !TryParseMaterial(original, bigEndian, out MaterialLayout target))
                throw new InvalidDataException(
                    "Material RAW doesn't match a supported, fully validated Unity Material layout " +
                    "(keyword arrays/string, texture, float and color tables). " +
                    "Import aborted WITHOUT a historical TypeTree search. Use TXT/JSON or both source TypeTrees.");
            if (donor.HasInts && !target.HasInts && donor.IntCount != 0)
                throw new InvalidDataException(
                    "Donor Material has non-empty m_Ints but destination Material has no m_Ints. " +
                    "Cannot safely discard material properties. Import aborted.");

            // Keep all donor serialized data except schema-specific keyword and
            // optional m_Ints framing. The destination m_Shader PPtr is restored.
            using var output = new MemoryStream(source.Length + 128);
            output.Write(source, 0, donor.KeywordsStart);
            if (target.KeywordsAsArrays)
            {
                IReadOnlyList<string> valid = donor.KeywordsAsArrays
                    ? donor.ValidKeywords : donor.Keywords;
                IReadOnlyList<string> invalid = donor.KeywordsAsArrays
                    ? donor.InvalidKeywords : Array.Empty<string>();
                WriteInt32(output, valid.Count, bigEndian);
                foreach (string keyword in valid)
                    WriteUnityString(output, keyword, bigEndian);
                WriteInt32(output, invalid.Count, bigEndian);
                foreach (string keyword in invalid)
                    WriteUnityString(output, keyword, bigEndian);
            }
            else
            {
                WriteUnityString(output, string.Join(" ", donor.Keywords), bigEndian);
            }
            output.Write(source, donor.KeywordsEnd, donor.IntsStart - donor.KeywordsEnd);
            if (target.HasInts)
            {
                if (donor.HasInts)
                    output.Write(source, donor.IntsStart, donor.IntsEnd - donor.IntsStart);
                else
                    WriteInt32(output, 0, bigEndian); // missing m_Ints defaults to []
            }
            output.Write(source, donor.IntsEnd, source.Length - donor.IntsEnd);
            prepared = output.ToArray();
            original.AsSpan(target.ShaderPointer, PPtrSize)
                .CopyTo(prepared.AsSpan(donor.ShaderPointer, PPtrSize));

            if (!TryParseMaterial(prepared, bigEndian, out MaterialLayout converted) ||
                converted.KeywordsAsArrays != target.KeywordsAsArrays ||
                converted.HasInts != target.HasInts ||
                converted.Keywords.Count != donor.Keywords.Count)
                throw new InvalidDataException(
                    "Material conversion did not reparse against the selected target wire format. " +
                    "Import aborted; asset unchanged.");

            diagnostic = $"Material RAW converted '{donor.Name}' -> '{target.Name}': " +
                         $"keywords {(donor.KeywordsAsArrays ? "arrays" : "string")} -> " +
                         $"{(target.KeywordsAsArrays ? "arrays" : "string")}, " +
                         $"m_Ints {(donor.HasInts ? "present" : "absent")} -> " +
                         $"{(target.HasInts ? "present" : "absent")}; " +
                         "destination m_Shader FileID/PathID preserved. " +
                         "Other donor texture PPtrs and external dependencies are NOT verified.";
            return true;
        }

        // No TypeTree can be inferred uniquely from an unrelated UMA *.dat.
        // Legacy RAW replacement is intentional here, and visibly unverified;
        // preserve destination m_Script so an exported donor cannot point at the
        // wrong MonoScript in the destination asset file.
        if (isMono && TryUmaMeshHeader(source, bigEndian, out string donorName))
        {
            byte[] original = ReadOriginalBytes(destination, 1024);
            if (!TryUmaMeshHeader(original, bigEndian, out string targetName))
                throw new InvalidDataException(
                    "Donor is a UMA *_Mesh MonoBehaviour, but the destination has no compatible " +
                    "m_Enabled / m_Script / m_Name header. Asset unchanged.");
            prepared = (byte[])source.Clone();
            original.AsSpan(MonoScriptPointerOffset, PPtrSize)
                .CopyTo(prepared.AsSpan(MonoScriptPointerOffset, PPtrSize));
            diagnostic = $"UMA Mesh RAW byte replacement '{donorName}' -> '{targetName}' " +
                         "(destination MonoBehaviour m_Script FileID/PathID preserved; " +
                         "source TypeTree layout NOT verified). Other PPtrs, mesh arrays " +
                         "and version-dependent fields were not migrated.";
            return true;
        }
        return false;
    }

    private static byte[] ReadOriginalBytes(AssetContainer destination, int maxBytes)
    {
        if (destination.Size == 0)
            throw new InvalidDataException("Destination asset is empty.");
        Stream stream = destination.FileReader?.BaseStream
            ?? throw new InvalidDataException("No source reader for destination asset.");
        if (!stream.CanRead || !stream.CanSeek)
            throw new InvalidDataException("Destination asset reader is not seekable/readable.");
        int size = (int)Math.Min((long)destination.Size, maxBytes);
        byte[] data = new byte[size];
        lock (stream)
        {
            long oldPosition = stream.Position;
            try
            {
                stream.Position = destination.FilePosition;
                stream.ReadExactly(data);
            }
            finally
            {
                stream.Position = oldPosition;
            }
        }
        return data;
    }

    private static bool TryUmaMeshHeader(ReadOnlySpan<byte> bytes, bool bigEndian, out string name)
    {
        name = string.Empty;
        if (bytes.Length < 44 || bytes[12] > 1 || bytes[13] != 0 ||
            bytes[14] != 0 || bytes[15] != 0)
            return false;
        if (!TryReadUnityStringAt28(bytes, bigEndian, out name))
            return false;
        return name.EndsWith("_Mesh", StringComparison.OrdinalIgnoreCase);
    }

    // Name at offset 28; using an actual local variable avoids mutable ref to
    // a field or a temporary (C# requires an assignable storage location).
    private static bool TryReadUnityStringAt28(ReadOnlySpan<byte> bytes, bool bigEndian, out string name)
    {
        int position = 28;
        return TryReadUnityString(bytes, ref position, bigEndian, out name);
    }

    private static bool TryParseMaterial(ReadOnlySpan<byte> data, bool bigEndian, out MaterialLayout result)
    {
        result = new MaterialLayout();
        int pos = 0;
        if (!TryReadUnityString(data, ref pos, bigEndian, out string name) || name.Length == 0)
            return false;
        if (data.Length - pos < PPtrSize) return false;
        int shaderPtr = pos;
        pos += PPtrSize;
        int keywordStart = pos;
        MaterialLayout? unique = null;

        // Two historical formats: m_ShaderKeywords string (Unity 2018), or
        // m_ValidKeywords[] + m_InvalidKeywords[] (newer Unity). Distinct
        // candidates are accepted only if exactly one consumes the full payload.
        for (int arrayFormat = 0; arrayFormat < 2; arrayFormat++)
        {
            int afterKeywords = keywordStart;
            List<string> words = new();
            List<string> validKeywords = new();
            List<string> invalidKeywords = new();
            bool parsedKeywords;
            if (arrayFormat == 0)
            {
                parsedKeywords = TryReadUnityString(data, ref afterKeywords, bigEndian, out string keywords);
                if (parsedKeywords)
                    words.AddRange(keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
            else
            {
                parsedKeywords = TryReadUnityStringArray(data, ref afterKeywords, bigEndian, validKeywords) &&
                                 TryReadUnityStringArray(data, ref afterKeywords, bigEndian, invalidKeywords);
                if (parsedKeywords)
                {
                    words.AddRange(validKeywords);
                    words.AddRange(invalidKeywords);
                }
            }
            if (!parsedKeywords) continue;
            for (int hasInts = 0; hasInts < 2; hasInts++)
            {
                if (!TryParseMaterialTail(data, afterKeywords, bigEndian, hasInts == 1,
                        out int intsStart, out int intsEnd, out int intCount))
                    continue;
                var candidate = new MaterialLayout
                {
                    Name = name,
                    ShaderPointer = shaderPtr,
                    KeywordsStart = keywordStart,
                    KeywordsEnd = afterKeywords,
                    KeywordsAsArrays = arrayFormat == 1,
                    Keywords = words,
                    ValidKeywords = validKeywords,
                    InvalidKeywords = invalidKeywords,
                    IntsStart = intsStart,
                    IntsEnd = intsEnd,
                    IntCount = intCount,
                    HasInts = hasInts == 1
                };
                if (unique != null) return false;
                unique = candidate;
            }
        }
        if (unique == null) return false;
        result = unique;
        return true;
    }

    private static bool TryParseMaterialTail(ReadOnlySpan<byte> data, int pos, bool bigEndian,
        bool hasInts, out int intsStart, out int intsEnd, out int intCount)
    {
        intsStart = intsEnd = intCount = 0;
        // m_LightmapFlags, m_EnableInstancingVariants/padding, m_CustomRenderQueue,
        // m_StringTagMap count, m_DisabledShaderPasses count. This layout has a
        // 20-byte fixed prefix in BOTH observed donor and target Material versions.
        if (data.Length - pos < 24) return false;
        pos += 12;
        if (!TrySkipStringMap(data, ref pos, bigEndian)) return false;
        if (!TrySkipStringArray(data, ref pos, bigEndian)) return false;
        if (!TryReadCount(data, ref pos, bigEndian, out int texCount, 20000)) return false;
        for (int i = 0; i < texCount; i++)
        {
            if (!TryReadUnityString(data, ref pos, bigEndian, out _) || data.Length - pos < 28)
                return false;
            pos += 28; // Texture PPtr, scale Vector2, offset Vector2
        }
        intsStart = pos;
        if (hasInts)
        {
            if (!TrySkipNamedScalars(data, ref pos, bigEndian, 4, out int ints)) return false;
            intCount = ints;
        }
        intsEnd = pos;
        if (!TrySkipNamedScalars(data, ref pos, bigEndian, 4, out _)) return false;
        if (!TrySkipNamedScalars(data, ref pos, bigEndian, 16, out _)) return false;
        if (data.Length - pos > 7) return false;
        for (int i = pos; i < data.Length; i++)
            if (data[i] != 0) return false;
        return true;
    }

    private static bool TrySkipStringMap(ReadOnlySpan<byte> data, ref int pos, bool bigEndian)
    {
        if (!TryReadCount(data, ref pos, bigEndian, out int count, 2000)) return false;
        for (int i = 0; i < count; i++)
            if (!TryReadUnityString(data, ref pos, bigEndian, out _) ||
                !TryReadUnityString(data, ref pos, bigEndian, out _)) return false;
        return true;
    }

    private static bool TrySkipStringArray(ReadOnlySpan<byte> data, ref int pos, bool bigEndian)
    {
        if (!TryReadCount(data, ref pos, bigEndian, out int count, 2000)) return false;
        for (int i = 0; i < count; i++)
            if (!TryReadUnityString(data, ref pos, bigEndian, out _)) return false;
        return true;
    }

    private static bool TryReadUnityStringArray(ReadOnlySpan<byte> data, ref int pos,
        bool bigEndian, List<string> output)
    {
        if (!TryReadCount(data, ref pos, bigEndian, out int count, 256)) return false;
        for (int i = 0; i < count; i++)
        {
            if (!TryReadUnityString(data, ref pos, bigEndian, out string word)) return false;
            if (word.Length > 0) output.Add(word);
        }
        return true;
    }

    private static bool TrySkipNamedScalars(ReadOnlySpan<byte> data, ref int pos,
        bool bigEndian, int valueSize, out int count)
    {
        count = 0;
        if (!TryReadCount(data, ref pos, bigEndian, out count, 20000)) return false;
        for (int i = 0; i < count; i++)
        {
            if (!TryReadUnityString(data, ref pos, bigEndian, out _) ||
                data.Length - pos < valueSize) return false;
            pos += valueSize;
        }
        return true;
    }

    private static bool TryReadCount(ReadOnlySpan<byte> data, ref int pos,
        bool bigEndian, out int count, int maxCount)
    {
        count = 0;
        if (data.Length - pos < 4) return false;
        count = bigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos, 4));
        pos += 4;
        return count >= 0 && count <= maxCount;
    }

    private static bool TryReadUnityString(ReadOnlySpan<byte> data, ref int pos,
        bool bigEndian, out string value)
    {
        value = string.Empty;
        if (!TryReadCount(data, ref pos, bigEndian, out int length, 65536) ||
            data.Length - pos < length)
            return false;
        var bytes = data.Slice(pos, length);
        foreach (byte b in bytes)
            if (b == 0 || b < 32 || b == 127) return false;
        value = Encoding.UTF8.GetString(bytes);
        int end = pos + length;
        int aligned = (end + 3) & ~3;
        if (aligned > data.Length) return false;
        for (int i = end; i < aligned; i++)
            if (data[i] != 0) return false;
        pos = aligned;
        return true;
    }

    private static void WriteInt32(Stream stream, int number, bool bigEndian)
    {
        Span<byte> word = stackalloc byte[4];
        if (bigEndian) BinaryPrimitives.WriteInt32BigEndian(word, number);
        else BinaryPrimitives.WriteInt32LittleEndian(word, number);
        stream.Write(word);
    }

    private static void WriteUnityString(Stream stream, string text, bool bigEndian)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        WriteInt32(stream, bytes.Length, bigEndian);
        stream.Write(bytes);
        for (int n = (int)((4 - (stream.Position & 3)) & 3); n > 0; n--)
            stream.WriteByte(0);
    }
}
