// Imported and adapted from UABEvol (2026-10-18): target TypeTree is authoritative.
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UABEAvalonia;

public enum UABEvolAssetImportSourceFormat
{
    Raw,
    Text,
    Json
}

public sealed class UABEvolCompatibleAssetImportReport
{
    public bool UsedValueReplacement { get; internal set; }
    public int ReplacedValues { get; internal set; }
    public int RebuiltArrays { get; internal set; }
    // Target-only nodes are never copied from the old asset in cross-version mode.
    // They stay at the default value produced by the TARGET TypeTree.
    public int MissingTargetFields { get; internal set; }
    public int IgnoredSourceFields { get; internal set; }
    public int ConvertedTypeValues { get; internal set; }
    public int NormalizedNameMatches { get; internal set; }
    public int SemanticMigrations { get; internal set; }
    public int ShaderProgramTablesVerified { get; internal set; }
    public int ShaderTargetFieldsPreserved { get; internal set; }
    public int RawCandidateLayouts { get; internal set; }
    public int RawSemanticVariants { get; internal set; }
    public int RawTrailingZeroBytes { get; internal set; }
    public int MonoBehaviourTargetFieldsPreserved { get; internal set; }
    // V35: verified, zero-filled UMA Mesh donor-only reserved words removed.
    public int RawUmaMeshReservedZeroBytesDropped { get; internal set; }

    public int DefaultedTargetFields => MissingTargetFields;

    public override string ToString()
    {
        if (!UsedValueReplacement)
            return "Direct import: source is already compatible with the selected asset TypeTree.";

        return $"Value replacement mode: replaced {ReplacedValues} value(s), " +
               $"rebuilt {RebuiltArrays} array(s), converted {ConvertedTypeValues} value type(s), " +
               $"defaulted {MissingTargetFields} target-only field(s), " +
               $"ignored {IgnoredSourceFields} source-only field(s), " +
               $"semantic migrations {SemanticMigrations}, " +
               $"shader verify {ShaderProgramTablesVerified}, target fields kept {ShaderTargetFieldsPreserved}, " +
               $"raw layouts {RawCandidateLayouts}/variants {RawSemanticVariants}, " +
               $"raw terminal-zero {RawTrailingZeroBytes} byte(s), UMA Mesh reserved-zero dropped {RawUmaMeshReservedZeroBytesDropped} byte(s), MonoBehaviour m_Script PPtr kept {MonoBehaviourTargetFieldsPreserved}.";
    }
}

/// <summary>
/// Imports values into the TypeTree of the currently selected asset. The selected
/// asset TypeTree is always authoritative: for a cross-version import a brand-new
/// default value tree is created from the TARGET template, then source values are
/// mapped by node name and converted to the target value type. Therefore:
///   - source-only/newer nodes are dropped;
///   - target-only nodes stay at target defaults (never retain stale old values);
///   - arrays are rebuilt from the source count using target element templates;
///   - changed primitive types are converted when the conversion is safe.
/// Matching prefers the same parent and exact name, then case-insensitive/canonical
/// unique names, and only then a unique descendant fallback.
/// </summary>
public sealed class UABEvolCompatibleAssetImporter
{
    private readonly byte[] _sourceData;
    private readonly RefTypeManager _refMan;
    private readonly bool _targetBigEndian;
    private readonly AssetsManager? _manager;
    private readonly int _targetTypeId;
    private readonly bool _preferEditorTemplate;

    // RAW exporters in the wild sometimes append a small, all-zero terminal block
    // after a MonoBehaviour object (the user's UMA Mesh sample has 16 bytes).
    // This is not object data when the TARGET TypeTree has already consumed the
    // complete object. Keep the tolerance deliberately small and MonoBehaviour-only.
    private const int MonoBehaviourRawTerminalZeroTolerance = 64;
    private bool IsMonoBehaviourTarget =>
        _targetTypeId == (int)AssetClassID.MonoBehaviour || _targetTypeId < 0;

    private static readonly string[] ShaderTargetOwnedFields =
    [
        "m_Dependencies",
        "m_NonModifiableTextures",
        "m_ShaderIsBaked",
    ];

    public UABEvolCompatibleAssetImporter(
        byte[] sourceData,
        RefTypeManager refMan,
        bool targetBigEndian,
        AssetsManager? manager = null,
        int targetTypeId = 0,
        bool preferEditorTemplate = false)
    {
        _sourceData = sourceData ?? throw new ArgumentNullException(nameof(sourceData));
        _refMan = refMan;
        _targetBigEndian = targetBigEndian;
        _manager = manager;
        _targetTypeId = targetTypeId;
        _preferEditorTemplate = preferEditorTemplate;
    }

    public byte[]? Import(
        AssetTypeValueField targetBaseField,
        UABEvolAssetImportSourceFormat format,
        out string? exceptionMessage,
        out UABEvolCompatibleAssetImportReport report)
    {
        report = new UABEvolCompatibleAssetImportReport();
        exceptionMessage = null;
        try
        {
            switch (format)
            {
                case UABEvolAssetImportSourceFormat.Json:
                    return ImportJson(targetBaseField, report);
                case UABEvolAssetImportSourceFormat.Text:
                    return ImportText(targetBaseField, report);
                case UABEvolAssetImportSourceFormat.Raw:
                    return ImportRaw(targetBaseField, report);
                default:
                    throw new NotSupportedException($"Unsupported import format {format}.");
            }
        }
        catch (Exception ex)
        {
            exceptionMessage = ex.ToString();
            return null;
        }
    }

    /// <summary>
    /// Cross-version value migration when the source is already deserialized.
    /// Import Pro uses this path because it has both source and destination TypeTrees
    /// in memory; unlike RAW import there is no need to guess the source layout.
    /// The destination TypeTree remains authoritative and source-only fields are
    /// ignored while target-only fields keep their default values.
    /// </summary>
    public AssetTypeValueField ImportValueField(
        AssetTypeValueField targetBaseField,
        AssetTypeValueField sourceField,
        out UABEvolCompatibleAssetImportReport report)
    {
        ArgumentNullException.ThrowIfNull(targetBaseField);
        ArgumentNullException.ThrowIfNull(sourceField);

        report = new UABEvolCompatibleAssetImportReport { UsedValueReplacement = true };
        AssetTypeValueField merged = CreateFreshTarget(targetBaseField);
        ApplyValueField(merged, sourceField, report, allowUniqueDescendantFallback: true);
        ApplyKnownValueSemanticMigrations(merged, targetBaseField, sourceField, report);
        FinalizeShaderImportIfNeeded(merged, targetBaseField, report, "IN-MEMORY TypeTree");
        EnsureFallbackChangedSomething(report, "IN-MEMORY TypeTree");
        return merged;
    }

    private byte[] ImportJson(AssetTypeValueField targetBaseField, UABEvolCompatibleAssetImportReport report)
    {
        string jsonText = DecodeText(_sourceData);
        JToken token = JToken.Parse(jsonText);

        // Shader import is always normalized through the TARGET TypeTree so JSON/TXT/RAW
        // share the same V12 legacy-safe semantics. A direct JSON writer can preserve a
        // foreign Shader program-table layout or foreign dependency PPtrs even when the
        // bytes happen to deserialize under the destination schema.
        if (_targetTypeId != (int)AssetClassID.Shader && !_targetBigEndian)
        {
            using var ms = new MemoryStream(_sourceData, writable: false);
            var directImporter = new UABEvolAssetImport(ms, _refMan);
            byte[]? direct = directImporter.ImportJsonAsset(targetBaseField.TemplateField, out _);
            if (direct != null)
            {
                report.UsedValueReplacement = false;
                return direct;
            }
        }

        AssetTypeValueField merged = CreateFreshTarget(targetBaseField);
        report.UsedValueReplacement = true;
        ApplyJsonToken(merged, token, report, allowUniqueDescendantFallback: true);
        ApplyKnownJsonSemanticMigrations(merged, targetBaseField, token, report);
        FinalizeShaderImportIfNeeded(merged, targetBaseField, report, "JSON");
        EnsureFallbackChangedSomething(report, "JSON");
        return merged.WriteToByteArray(_targetBigEndian);
    }

    private byte[] ImportText(AssetTypeValueField targetBaseField, UABEvolCompatibleAssetImportReport report)
    {
        string text = DecodeText(_sourceData);
        DumpNode root = ParseTextDump(text);
        DumpNode sourceRoot = SelectTextRoot(root, targetBaseField.FieldName);

        // Shader TXT follows the same typed TARGET-schema path as JSON and RAW. This
        // avoids one format silently bypassing program-table migration/target-PPtr
        // preservation while another format performs the safe conversion.
        if (_targetTypeId != (int)AssetClassID.Shader &&
            !_targetBigEndian && IsTextShapeCompatible(targetBaseField, sourceRoot))
        {
            using var ms = new MemoryStream(_sourceData, writable: false);
            var directImporter = new UABEvolAssetImport(ms, _refMan);
            byte[]? direct = directImporter.ImportTextAsset(out _);
            if (direct != null && IsBinaryCompatible(targetBaseField.TemplateField, direct))
            {
                report.UsedValueReplacement = false;
                return direct;
            }
        }

        AssetTypeValueField merged = CreateFreshTarget(targetBaseField);
        report.UsedValueReplacement = true;
        ApplyTextNode(merged, sourceRoot, report, allowUniqueDescendantFallback: true);
        ApplyKnownTextSemanticMigrations(merged, targetBaseField, sourceRoot, report);
        FinalizeShaderImportIfNeeded(merged, targetBaseField, report, "TXT");
        EnsureFallbackChangedSomething(report, "TXT");
        return merged.WriteToByteArray(_targetBigEndian);
    }

    private byte[] ImportRaw(AssetTypeValueField targetBaseField, UABEvolCompatibleAssetImportReport report)
    {
        // AnimationClip RAW/DAT/BIN is intentionally a byte-for-byte replacement.
        // AnimationClip layouts across nearby Unity versions can often deserialize
        // the exact same payload length (the historical resolver may therefore find
        // several equally valid candidates). For this asset class the user's RAW
        // export is the authoritative serialized object, matching classic UABE raw
        // import semantics: do not guess, migrate, reinterpret or rewrite fields.
        if (_targetTypeId == (int)AssetClassID.AnimationClip)
        {
            if (_sourceData.Length == 0)
                throw new InvalidDataException("AnimationClip RAW/DAT/BIN payload is empty.");

            report.UsedValueReplacement = false;
            return (byte[])_sourceData.Clone();
        }

        // RAW/DAT/BIN has no node names or Unity version metadata. Resolve it only
        // when exactly one distinct TypeTree layout can parse the payload. If more
        // than one historical layout fits, choosing one would be guessing and could
        // reinterpret same-sized fields (for example int <-> float).
        //
        // MonoBehaviour is a special-but-safe case: some UABE/third-party RAW exports
        // retain a short all-zero terminal block after the serialized object. The target
        // TypeTree is still authoritative; accept that padding only when every trailing
        // byte is zero and the target schema consumed the complete object before it.
        if (IsMonoBehaviourTarget &&
            TryDeserializeWithTerminalZeroPadding(
                targetBaseField.TemplateField,
                _sourceData,
                _targetBigEndian,
                MonoBehaviourRawTerminalZeroTolerance,
                out _,
                out int monoTrailingZeroBytes))
        {
            report.RawTrailingZeroBytes = monoTrailingZeroBytes;
        }

        // V35: An older/newer UMA Mesh RAW exporter may contain four zero-filled
        // donor-only 32-bit fields immediately before extraBindPoses. These are NOT
        // trailing zero padding: without a verified normalization, the target tree
        // stops early and imports must still fail rather than guess a schema.
        AssetTypeValueField? sourceField = null;
        bool sourceUsesTargetTemplate = false;
        int candidateCount = 0;
        int umaReservedBytesDropped = 0;
        bool rawResolved = IsMonoBehaviourTarget &&
            TryRecoverUmaMeshReservedZeroFields(
                targetBaseField,
                out sourceField,
                out umaReservedBytesDropped);
        if (rawResolved)
        {
            sourceUsesTargetTemplate = true;
            candidateCount = 1;
            report.RawUmaMeshReservedZeroBytesDropped = umaReservedBytesDropped;
        }
        else
        {
            rawResolved = TryResolveRawSourceField(
                targetBaseField,
                out sourceField,
                out sourceUsesTargetTemplate,
                out candidateCount);
        }
        report.RawCandidateLayouts = candidateCount;
        if (_targetTypeId == (int)AssetClassID.Shader)
            report.RawSemanticVariants = candidateCount;

        if (!rawResolved)
        {
            if (candidateCount > 1)
            {
                string kind = _targetTypeId == (int)AssetClassID.Shader
                    ? "semantic Shader interpretations"
                    : "distinct Unity TypeTree layouts";
                throw new InvalidDataException(
                    $"RAW/DAT/BIN payload is ambiguous: {candidateCount} {kind} remain after exact parse/validation. " +
                    "UABEvol will not guess foreign wire bytes. Use JSON/TXT when the RAW source schema cannot be proven.");
            }

            throw new InvalidDataException(
                _targetTypeId == (int)AssetClassID.Shader
                    ? "Shader RAW/DAT/BIN không khớp TypeTree lịch sử nào có program table/blob hợp lệ. Import đã bị hủy trước khi ghi object."
                    : IsMonoBehaviourTarget
                        ? $"MonoBehaviour RAW/DAT/BIN không khớp duy nhất TypeTree đích (bytes={_sourceData.Length}, layoutCandidates={candidateCount}). Đã thử terminal zero-padding và UMA Mesh reserved-zero V35 (chỉ nhận byte-exact). Không đoán schema nguồn; hãy dùng JSON/TXT hoặc donor TypeTree/source asset khi layout thực sự khác."
                        : "Raw payload does not match a unique destination/historical TypeTree. " +
                          "RAW/DAT/BIN files do not embed node names; use the source JSON/TXT dump for safe value replacement.");
        }

        if (sourceUsesTargetTemplate &&
            _targetTypeId != (int)AssetClassID.Shader &&
            !IsMonoBehaviourTarget)
        {
            // Exact non-Shader/non-MonoBehaviour target layout: the bytes were
            // validated against the destination TypeTree and no competing historical
            // layout was found, so classic direct RAW replacement remains safe.
            report.UsedValueReplacement = false;
            return (byte[])_sourceData.Clone();
        }

        AssetTypeValueField merged = CreateFreshTarget(targetBaseField);
        report.UsedValueReplacement = true;
        ApplyValueField(merged, sourceField!, report, allowUniqueDescendantFallback: true);
        ApplyKnownValueSemanticMigrations(merged, targetBaseField, sourceField!, report);

        // MonoBehaviour RAW imports retain exactly the TARGET m_Script PPtr.
        // All other PPtrs (including m_GameObject and custom data links) come
        // from RAW and must not be overwritten with values from the target.
        // This does not modify the destination object's own AssetFileInfo.PathID.
        FinalizeMonoBehaviourRawImportIfNeeded(merged, targetBaseField, report);
        FinalizeShaderImportIfNeeded(merged, targetBaseField, report, "RAW");
        EnsureFallbackChangedSomething(report, "RAW");
        return merged.WriteToByteArray(_targetBigEndian);
    }

    // V35: guarded compatibility for UMA Mesh donor RAW with 16 bytes of
    // donor-only zero-valued reserved fields directly before extraBindPoses.
    // A candidate is accepted ONLY when the destination TypeTree can fully
    // deserialize the normalized bytes, serializes them back byte-exactly, and
    // the bind-pose/hash arrays agree and extraBones remains a plausible
    // nonzero count (it may have one extra root bone). Different
    // candidates producing different bytes are ambiguous and are rejected.
    // No header/PPtr fields are adjusted here. m_Script alone is restored later.
    private bool TryRecoverUmaMeshReservedZeroFields(
        AssetTypeValueField targetBaseField,
        out AssetTypeValueField? recovered,
        out int zeroBytesDropped)
    {
        recovered = null;
        zeroBytesDropped = 0;
        if (_sourceData.Length < 128 || _targetBigEndian)
            return false;

        AssetTypeValueField meshData = targetBaseField["meshData"];
        if (meshData.IsDummy ||
            meshData["optimizedSubMeshArray"].IsDummy ||
            meshData["extraBindPoses"].IsDummy ||
            meshData["extraBoneNameHashes"].IsDummy ||
            meshData["extraBones"].IsDummy)
            return false;

        // If RAW is already valid against the destination, do not alter it.
        if (TryDeserializeWithTerminalZeroPadding(
                targetBaseField.TemplateField,
                _sourceData, _targetBigEndian,
                MonoBehaviourRawTerminalZeroTolerance, out _, out _))
            return false;

        long consumed;
        try
        {
            using var input = new MemoryStream(_sourceData, writable: false);
            using var reader = new AssetsFileReader(input) { BigEndian = _targetBigEndian };
            _ = targetBaseField.TemplateField.MakeValue(reader, _refMan);
            consumed = reader.Position;
        }
        catch
        {
            return false;
        }

        // A clean-but-short parse is the characteristic signature. Limit the
        // probe to the final 96 consumed bytes; never brute-force the mesh body.
        if (consumed < 96 || consumed >= _sourceData.LongLength - 64)
            return false;

        const int extraZeroBytes = 16;
        long scanStart = Math.Max(0, consumed - 96);
        scanStart = (scanStart + 3) & ~3L;
        byte[]? uniqueNormalized = null;
        AssetTypeValueField? uniqueField = null;

        for (long p = scanStart; p + extraZeroBytes <= consumed; p += 4)
        {
            int start = checked((int)p);
            bool allZero = true;
            for (int j = 0; j < extraZeroBytes; j++)
            {
                if (_sourceData[start + j] != 0) { allZero = false; break; }
            }
            if (!allZero)
                continue;

            byte[] candidate = new byte[_sourceData.Length - extraZeroBytes];
            Buffer.BlockCopy(_sourceData, 0, candidate, 0, start);
            Buffer.BlockCopy(_sourceData, start + extraZeroBytes,
                candidate, start, candidate.Length - start);

            if (!TryDeserializeWithTerminalZeroPadding(
                    targetBaseField.TemplateField, candidate, _targetBigEndian,
                    maxTrailingZeroBytes: 0, out AssetTypeValueField? parsed, out _) ||
                parsed == null)
                continue;

            AssetTypeValueField parsedMesh = parsed["meshData"];
            if (parsedMesh.IsDummy)
                continue;

            AssetTypeValueField binds = GetNamedArrayNode(parsedMesh, "extraBindPoses");
            AssetTypeValueField hashes = GetNamedArrayNode(parsedMesh, "extraBoneNameHashes");
            AssetTypeValueField bones = GetNamedArrayNode(parsedMesh, "extraBones");
            if (binds.IsDummy || hashes.IsDummy || bones.IsDummy ||
                binds.Children.Count == 0 ||
                binds.Children.Count != hashes.Children.Count ||
                bones.Children.Count == 0 ||
                bones.Children.Count > binds.Children.Count + 2)
                continue;

            // This is essential: a syntactically parsed but silently truncated
            // candidate is NOT evidence of a compatible schema.
            byte[] roundTrip;
            try { roundTrip = parsed.WriteToByteArray(_targetBigEndian); }
            catch { continue; }
            if (!roundTrip.AsSpan().SequenceEqual(candidate))
                continue;

            if (uniqueNormalized != null)
            {
                if (!uniqueNormalized.AsSpan().SequenceEqual(candidate))
                    return false; // multiple distinct valid interpretations
                continue;
            }

            uniqueNormalized = candidate;
            uniqueField = parsed;
        }

        if (uniqueField == null)
            return false;

        recovered = uniqueField;
        zeroBytesDropped = extraZeroBytes;
        return true;
    }

    private static AssetTypeValueField CreateFreshTarget(AssetTypeValueField targetBaseField)
    {
        // Never clone the current values here. Cross-version import is a VALUE
        // migration into the destination TypeTree, not a merge with stale values.
        // Missing destination fields must therefore remain Unity/TypeTree defaults.
        return ValueBuilder.DefaultValueFieldFromTemplate(targetBaseField.TemplateField);
    }

    private bool TryResolveRawSourceField(
        AssetTypeValueField targetBaseField,
        out AssetTypeValueField? sourceField,
        out bool sourceUsesTargetTemplate,
        out int candidateCount)
    {
        sourceField = null;
        sourceUsesTargetTemplate = false;
        candidateCount = 0;

        var candidates = new Dictionary<string, (AssetTypeValueField Field, bool IsTarget)>(StringComparer.Ordinal);
        string targetSignature = BuildTemplateSignature(targetBaseField.TemplateField);

        bool targetParsedOk;
        AssetTypeValueField? targetParsed;
        if (IsMonoBehaviourTarget)
        {
            targetParsedOk = TryDeserializeWithTerminalZeroPadding(
                targetBaseField.TemplateField,
                _sourceData,
                _targetBigEndian,
                MonoBehaviourRawTerminalZeroTolerance,
                out targetParsed,
                out _);
        }
        else
        {
            targetParsedOk = TryDeserializeExact(
                targetBaseField.TemplateField,
                _sourceData,
                _targetBigEndian,
                out targetParsed);
        }

        if (targetParsedOk && targetParsed != null)
            candidates[targetSignature] = (targetParsed, true);

        ClassPackageFile? package = _manager?.ClassPackage;
        if (package?.TpkTypeTree?.ClassInformation != null)
        {
            int fixedTypeId = _targetTypeId == (int)AssetClassID.MonoBehaviour || _targetTypeId < 0
                ? (int)AssetClassID.MonoBehaviour
                : _targetTypeId;

            ClassPackageClassInfo? classInfo = package.TpkTypeTree.ClassInformation
                .FirstOrDefault(c => c.ClassId == fixedTypeId);

            if (classInfo != null)
            {
                foreach (var versionEntry in classInfo.Classes)
                {
                    if (versionEntry.Value == null)
                        continue;

                    try
                    {
                        ClassDatabaseFile cldb = package.GetClassDatabase(versionEntry.Key);
                        ClassDatabaseType? cldbType = cldb.FindAssetClassByID(fixedTypeId);
                        if (cldbType == null)
                            continue;

                        var template = new AssetTypeTemplateField();
                        template.FromClassDatabase(cldb, cldbType, _preferEditorTemplate);

                        string signature = BuildTemplateSignature(template);
                        if (candidates.ContainsKey(signature))
                            continue;

                        AssetTypeValueField? parsed;
                        bool parsedOk = TryDeserializeExact(template, _sourceData, _targetBigEndian, out parsed);
                        if (!parsedOk)
                        {
                            // Legacy/console assets may use the opposite byte order.
                            parsedOk = TryDeserializeExact(template, _sourceData, !_targetBigEndian, out parsed);
                        }

                        if (parsedOk && parsed != null)
                            candidates[signature] = (parsed, signature == targetSignature);
                    }
                    catch
                    {
                        // Ignore one unsupported historical class database entry.
                    }
                }
            }
        }

        candidateCount = candidates.Count;

        if (_targetTypeId == (int)AssetClassID.Shader)
        {
            // RAW has no field names/version marker. For Shader, however, we can do
            // better than the old "exactly one TypeTree or fail" rule: every candidate
            // must first have a coherent program table/blob, then it is projected into
            // the TARGET schema using the same migration path as JSON/TXT. If several
            // historical layouts collapse to the same target bytes they are semantically
            // equivalent for this import and are safe to accept.
            return TryResolveShaderRawSourceField(
                targetBaseField,
                candidates.Values.ToList(),
                out sourceField,
                out sourceUsesTargetTemplate,
                out candidateCount);
        }

        if (candidateCount != 1)
            return false;

        var only = candidates.Values.First();
        sourceField = only.Field;
        sourceUsesTargetTemplate = only.IsTarget;
        return true;
    }

    private bool TryResolveShaderRawSourceField(
        AssetTypeValueField targetBaseField,
        IReadOnlyList<(AssetTypeValueField Field, bool IsTarget)> candidates,
        out AssetTypeValueField? sourceField,
        out bool sourceUsesTargetTemplate,
        out int candidateCount)
    {
        sourceField = null;
        sourceUsesTargetTemplate = false;
        candidateCount = candidates.Count;
        if (candidates.Count == 0)
            return false;

        var valid = new List<(AssetTypeValueField Field, bool IsTarget, byte[] Canonical, int Score)>();
        foreach (var candidate in candidates)
        {
            if (!ValidateShaderProgramLayout(candidate.Field, out _, out int sourceBlocks))
                continue;

            try
            {
                var localReport = new UABEvolCompatibleAssetImportReport { UsedValueReplacement = true };
                AssetTypeValueField merged = CreateFreshTarget(targetBaseField);
                ApplyValueField(merged, candidate.Field, localReport, allowUniqueDescendantFallback: true);
                ApplyKnownValueSemanticMigrations(merged, targetBaseField, candidate.Field, localReport);
                RestoreShaderTargetOwnedFields(merged, targetBaseField, localReport);

                if (!ValidateShaderProgramLayout(merged, out _, out int targetBlocks))
                    continue;

                byte[] canonical = merged.WriteToByteArray(_targetBigEndian);
                int score =
                    localReport.ReplacedValues * 4 +
                    localReport.RebuiltArrays * 32 +
                    localReport.SemanticMigrations * 64 +
                    Math.Min(sourceBlocks, targetBlocks) * 8 -
                    localReport.MissingTargetFields * 2;
                valid.Add((candidate.Field, candidate.IsTarget, canonical, score));
            }
            catch
            {
                // One historical schema can parse bytes syntactically but still fail
                // Shader semantic migration. It is not a viable RAW interpretation.
            }
        }

        candidateCount = valid.Count;
        if (valid.Count == 0)
            return false;

        // Collapse candidates that produce identical TARGET bytes. Adjacent Unity
        // versions often carry different TypeTree signatures with identical Shader
        // semantics; treating them as ambiguity made RAW needlessly fail.
        var variants = new List<List<(AssetTypeValueField Field, bool IsTarget, byte[] Canonical, int Score)>>();
        foreach (var item in valid.OrderByDescending(v => v.Score))
        {
            List<(AssetTypeValueField Field, bool IsTarget, byte[] Canonical, int Score)>? bucket =
                variants.FirstOrDefault(v => v[0].Canonical.AsSpan().SequenceEqual(item.Canonical));
            if (bucket == null)
            {
                bucket = [];
                variants.Add(bucket);
            }
            bucket.Add(item);
        }

        if (variants.Count == 1)
        {
            var chosen = variants[0].OrderByDescending(v => v.Score).First();
            sourceField = chosen.Field;
            // Force Shader RAW through typed normalization even when the candidate used
            // the target template, so dependencies/state and program tables match JSON/TXT.
            sourceUsesTargetTemplate = false;
            candidateCount = 1;
            return true;
        }

        // If variants differ, accept only a clearly better structural interpretation.
        // A one-point/tiny tie is not enough evidence to reinterpret foreign RAW bytes.
        var ranked = variants
            .Select(v => (Bucket: v, Score: v.Max(x => x.Score)))
            .OrderByDescending(v => v.Score)
            .ToList();
        if (ranked.Count >= 2 && ranked[0].Score >= ranked[1].Score + 64)
        {
            var chosen = ranked[0].Bucket.OrderByDescending(v => v.Score).First();
            sourceField = chosen.Field;
            sourceUsesTargetTemplate = false;
            candidateCount = variants.Count;
            return true;
        }

        candidateCount = variants.Count;
        return false;
    }

    private bool IsBinaryCompatible(AssetTypeTemplateField template, byte[] data)
        => TryDeserializeExact(template, data, _targetBigEndian, out _);

    private bool TryDeserializeExact(
        AssetTypeTemplateField template,
        byte[] data,
        bool bigEndian,
        out AssetTypeValueField? parsed)
        => TryDeserializeWithTerminalZeroPadding(
            template,
            data,
            bigEndian,
            maxTrailingZeroBytes: 7,
            out parsed,
            out _);

    private bool TryDeserializeWithTerminalZeroPadding(
        AssetTypeTemplateField template,
        byte[] data,
        bool bigEndian,
        int maxTrailingZeroBytes,
        out AssetTypeValueField? parsed,
        out int trailingZeroBytes)
    {
        parsed = null;
        trailingZeroBytes = 0;
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            using var reader = new AssetsFileReader(ms) { BigEndian = bigEndian };
            parsed = template.MakeValue(reader, _refMan);

            long trailing = ms.Length - reader.Position;
            if (trailing == 0)
                return true;

            // RAW exporters can retain terminal alignment/reserved bytes. Never accept
            // arbitrary trailing content: the target TypeTree must have parsed cleanly
            // and every remaining byte must be zero. The ordinary path stays at seven
            // bytes; only MonoBehaviour target resolution opts into the 64-byte cap.
            if (trailing < 0 || trailing > maxTrailingZeroBytes || trailing > int.MaxValue)
            {
                parsed = null;
                return false;
            }

            long tailStart = reader.Position;
            while (reader.Position < ms.Length)
            {
                if (reader.ReadByte() != 0)
                {
                    parsed = null;
                    trailingZeroBytes = 0;
                    return false;
                }
            }

            trailingZeroBytes = checked((int)(ms.Length - tailStart));
            return true;
        }
        catch
        {
            parsed = null;
            trailingZeroBytes = 0;
            return false;
        }
    }

    private static void ApplyValueField(
        AssetTypeValueField target,
        AssetTypeValueField source,
        UABEvolCompatibleAssetImportReport report,
        bool allowUniqueDescendantFallback)
    {
        if (target.TemplateField.IsArray)
        {
            // Byte vectors are special: AssetsTools may expose the source as a wrapper
            // with an "Array" child even when source.TemplateField.IsArray is false.
            // Read the payload first instead of rejecting that representation mismatch.
            if (target.TemplateField.ValueType == AssetValueType.ByteArray)
            {
                if (TryExtractBytePayload(source, out byte[] bytes))
                {
                    target.AsByteArray = bytes;
                    report.ReplacedValues += bytes.Length;
                    report.RebuiltArrays++;
                }
                else
                {
                    report.MissingTargetFields++;
                }
                return;
            }

            if (!source.TemplateField.IsArray)
            {
                report.MissingTargetFields++;
                return;
            }

            var newChildren = new List<AssetTypeValueField>(source.Children.Count);
            for (int i = 0; i < source.Children.Count; i++)
            {
                AssetTypeValueField targetElement = ValueBuilder.DefaultValueFieldFromArrayTemplate(target);
                ApplyValueField(targetElement, source.Children[i], report, allowUniqueDescendantFallback: false);
                newChildren.Add(targetElement);
            }
            target.Children = newChildren;
            target.AsArray = new AssetTypeArrayInfo(newChildren.Count);
            report.RebuiltArrays++;
            return;
        }

        if (IsLeaf(target))
        {
            if (TryAssignValueField(target, source, out bool convertedType))
            {
                report.ReplacedValues++;
                if (convertedType)
                    report.ConvertedTypeValues++;
            }
            else
            {
                report.MissingTargetFields++;
            }
            return;
        }

        foreach (AssetTypeValueField child in target.Children)
        {
            AssetTypeValueField? sourceChild = FindDirectValueChild(source, child.FieldName, report);
            if (sourceChild == null && allowUniqueDescendantFallback)
                sourceChild = FindUniqueValueDescendant(source, child.FieldName, report);

            if (sourceChild == null)
            {
                // Unity sometimes inserts/removes a wrapper struct while keeping its
                // child fields. Try mapping the child's descendants against the same
                // source scope before treating the whole target branch as new/default.
                if (!IsLeaf(child) && !child.TemplateField.IsArray)
                {
                    var bridgeReport = new UABEvolCompatibleAssetImportReport { UsedValueReplacement = true };
                    ApplyValueField(child, source, bridgeReport, allowUniqueDescendantFallback: true);
                    if (ReportHasDataChanges(bridgeReport))
                    {
                        MergeReport(report, bridgeReport);
                        continue;
                    }
                }

                report.MissingTargetFields += CountLeafFields(child);
                continue;
            }

            ApplyValueField(child, sourceChild, report, allowUniqueDescendantFallback);
        }
    }

    private static bool TryAssignValueField(
        AssetTypeValueField target,
        AssetTypeValueField source,
        out bool convertedType)
    {
        convertedType = target.TemplateField.ValueType != source.TemplateField.ValueType;
        try
        {
            // ByteArray sources can be materialized only as UInt8 children with no scalar
            // Value object. Handle them before the generic leaf-value guard.
            if (target.TemplateField.ValueType == AssetValueType.ByteArray)
            {
                if (TryExtractBytePayload(source, out byte[] bytePayload))
                {
                    target.AsByteArray = bytePayload;
                    convertedType = source.TemplateField.ValueType != AssetValueType.ByteArray;
                    return true;
                }
                return false;
            }

            if (source.Value == null)
                return false;

            AssetValueType sourceType = source.TemplateField.ValueType;
            object scalar = sourceType == AssetValueType.String
                ? source.AsString
                : source.AsObject;

            switch (target.TemplateField.ValueType)
            {
                case AssetValueType.Bool:
                    if (scalar is string boolText)
                    {
                        if (bool.TryParse(boolText, out bool parsedBool)) { target.AsBool = parsedBool; return true; }
                        if (long.TryParse(boolText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long boolInt)) { target.AsBool = boolInt != 0; return true; }
                        return false;
                    }
                    target.AsBool = Convert.ToBoolean(scalar, CultureInfo.InvariantCulture); return true;
                case AssetValueType.Int8: target.AsSByte = checked(Convert.ToSByte(scalar, CultureInfo.InvariantCulture)); return true;
                case AssetValueType.UInt8: target.AsByte = checked(Convert.ToByte(scalar, CultureInfo.InvariantCulture)); return true;
                case AssetValueType.Int16: target.AsShort = checked(Convert.ToInt16(scalar, CultureInfo.InvariantCulture)); return true;
                case AssetValueType.UInt16: target.AsUShort = checked(Convert.ToUInt16(scalar, CultureInfo.InvariantCulture)); return true;
                case AssetValueType.Int32: target.AsInt = checked(Convert.ToInt32(scalar, CultureInfo.InvariantCulture)); return true;
                case AssetValueType.UInt32: target.AsUInt = checked(Convert.ToUInt32(scalar, CultureInfo.InvariantCulture)); return true;
                case AssetValueType.Int64: target.AsLong = Convert.ToInt64(scalar, CultureInfo.InvariantCulture); return true;
                case AssetValueType.UInt64: target.AsULong = Convert.ToUInt64(scalar, CultureInfo.InvariantCulture); return true;
                case AssetValueType.Float: target.AsFloat = Convert.ToSingle(scalar, CultureInfo.InvariantCulture); return true;
                case AssetValueType.Double: target.AsDouble = Convert.ToDouble(scalar, CultureInfo.InvariantCulture); return true;
                case AssetValueType.String:
                    target.AsString = sourceType == AssetValueType.String
                        ? source.AsString
                        : Convert.ToString(scalar, CultureInfo.InvariantCulture) ?? string.Empty;
                    return true;
                case AssetValueType.ByteArray:
                    return false; // handled before the scalar-value guard above
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static AssetTypeValueField? FindDirectValueChild(
        AssetTypeValueField field,
        string name,
        UABEvolCompatibleAssetImportReport? report = null)
    {
        AssetTypeValueField? exact = field.Children.FirstOrDefault(c => c.FieldName == name);
        if (exact != null)
            return exact;

        AssetTypeValueField? caseInsensitive = field.Children.FirstOrDefault(c =>
            string.Equals(c.FieldName, name, StringComparison.OrdinalIgnoreCase));
        if (caseInsensitive != null)
            return caseInsensitive;

        foreach (string alias in GetFieldAliases(name))
        {
            AssetTypeValueField? aliased = field.Children.FirstOrDefault(c =>
                string.Equals(c.FieldName, alias, StringComparison.OrdinalIgnoreCase));
            if (aliased != null)
            {
                if (report != null)
                    report.SemanticMigrations++;
                return aliased;
            }
        }

        string canonical = CanonicalFieldName(name);
        if (canonical.Length == 0)
            return null;

        var canonicalMatches = field.Children
            .Where(c => CanonicalFieldName(c.FieldName) == canonical)
            .Take(2)
            .ToList();
        if (canonicalMatches.Count == 1)
        {
            if (report != null)
                report.NormalizedNameMatches++;
            return canonicalMatches[0];
        }
        return null;
    }

    private static AssetTypeValueField? FindUniqueValueDescendant(
        AssetTypeValueField field,
        string name,
        UABEvolCompatibleAssetImportReport? report = null)
    {
        var matches = new List<AssetTypeValueField>(2);
        CollectValueDescendants(field, name, matches, 2, canonical: false);
        if (matches.Count == 1)
            return matches[0];
        if (matches.Count > 1)
            return null;

        matches.Clear();
        CollectValueDescendants(field, name, matches, 2, canonical: true);
        if (matches.Count == 1)
        {
            if (report != null)
                report.NormalizedNameMatches++;
            return matches[0];
        }
        return null;
    }

    private static void CollectValueDescendants(
        AssetTypeValueField field,
        string name,
        List<AssetTypeValueField> matches,
        int limit,
        bool canonical)
    {
        string canonicalName = canonical ? CanonicalFieldName(name) : string.Empty;
        foreach (AssetTypeValueField child in field.Children)
        {
            bool same = canonical
                ? canonicalName.Length != 0 && CanonicalFieldName(child.FieldName) == canonicalName
                : string.Equals(child.FieldName, name, StringComparison.OrdinalIgnoreCase);
            if (same)
            {
                matches.Add(child);
                if (matches.Count >= limit)
                    return;
            }
            CollectValueDescendants(child, name, matches, limit, canonical);
            if (matches.Count >= limit)
                return;
        }
    }

    private static string CanonicalFieldName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        string value = name.Trim();
        if (value.StartsWith("m_", StringComparison.OrdinalIgnoreCase))
            value = value.Substring(2);

        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static int CountMatchingValueFields(AssetTypeValueField target, AssetTypeValueField source)
    {
        int score = 0;
        foreach (AssetTypeValueField child in target.Children)
        {
            AssetTypeValueField? match = FindDirectValueChild(source, child.FieldName);
            if (match == null)
                continue;
            score++;
            score += CountMatchingValueFields(child, match);
        }
        return score;
    }

    private static string BuildTemplateSignature(AssetTypeTemplateField field)
    {
        var sb = new StringBuilder();
        AppendTemplateSignature(field, sb);
        return sb.ToString();
    }

    private static void AppendTemplateSignature(AssetTypeTemplateField field, StringBuilder sb)
    {
        sb.Append(field.Name).Append(':').Append(field.Type).Append(':')
          .Append(field.IsArray ? 'A' : 'V').Append(';');
        foreach (AssetTypeTemplateField child in field.Children)
            AppendTemplateSignature(child, sb);
        sb.Append('|');
    }

    private static void ApplyJsonToken(
        AssetTypeValueField target,
        JToken source,
        UABEvolCompatibleAssetImportReport report,
        bool allowUniqueDescendantFallback)
    {
        if (target.TemplateField.IsArray)
        {
            if (source is not JArray array)
            {
                report.MissingTargetFields++;
                return;
            }

            if (target.TemplateField.ValueType == AssetValueType.ByteArray)
            {
                var bytes = new byte[array.Count];
                for (int i = 0; i < array.Count; i++)
                    bytes[i] = ConvertToByte(array[i]);
                target.AsByteArray = bytes;
                report.ReplacedValues += bytes.Length;
                report.RebuiltArrays++;
                return;
            }

            var newChildren = new List<AssetTypeValueField>(array.Count);
            for (int i = 0; i < array.Count; i++)
            {
                AssetTypeValueField element = ValueBuilder.DefaultValueFieldFromArrayTemplate(target);
                ApplyJsonToken(element, array[i], report, allowUniqueDescendantFallback: false);
                newChildren.Add(element);
            }
            target.Children = newChildren;
            target.AsArray = new AssetTypeArrayInfo(newChildren.Count);
            report.RebuiltArrays++;
            return;
        }

        if (IsLeaf(target))
        {
            if (TryAssignJsonValue(target, source))
            {
                report.ReplacedValues++;
                if (JsonValueRequiredCoercion(target.TemplateField.ValueType, source))
                    report.ConvertedTypeValues++;
            }
            else
            {
                report.MissingTargetFields++;
            }
            return;
        }

        if (source is not JObject obj)
        {
            // Array element dumps sometimes wrap an object in a single property named
            // exactly like the element field. Unwrap it before giving up.
            if (source is JProperty prop)
                source = prop.Value;
            if (source is not JObject)
            {
                report.MissingTargetFields += CountLeafFields(target);
                return;
            }
            obj = (JObject)source;
        }

        var directlyUsed = new HashSet<JToken>();
        foreach (AssetTypeValueField child in target.Children)
        {
            JToken? childToken = FindDirectJsonProperty(obj, child.FieldName, report);
            if (childToken == null && allowUniqueDescendantFallback)
                childToken = FindUniqueJsonDescendant(obj, child.FieldName, report);

            if (childToken == null)
            {
                if (!IsLeaf(child) && !child.TemplateField.IsArray)
                {
                    var bridgeReport = new UABEvolCompatibleAssetImportReport { UsedValueReplacement = true };
                    ApplyJsonToken(child, obj, bridgeReport, allowUniqueDescendantFallback: true);
                    if (ReportHasDataChanges(bridgeReport))
                    {
                        MergeReport(report, bridgeReport);
                        continue;
                    }
                }

                report.MissingTargetFields += CountLeafFields(child);
                continue;
            }

            directlyUsed.Add(childToken);
            ApplyJsonToken(child, childToken, report, allowUniqueDescendantFallback);
        }

        report.IgnoredSourceFields += obj.Properties().Count(p => !directlyUsed.Contains(p.Value));
    }

    private static JToken? FindDirectJsonProperty(
        JObject obj,
        string name,
        UABEvolCompatibleAssetImportReport? report = null)
    {
        JProperty? exact = obj.Properties().FirstOrDefault(p => p.Name == name);
        if (exact != null)
            return exact.Value;

        JProperty? caseInsensitive = obj.Properties().FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (caseInsensitive != null)
            return caseInsensitive.Value;

        foreach (string alias in GetFieldAliases(name))
        {
            JProperty? aliased = obj.Properties().FirstOrDefault(p =>
                string.Equals(p.Name, alias, StringComparison.OrdinalIgnoreCase));
            if (aliased != null)
            {
                if (report != null)
                    report.SemanticMigrations++;
                return aliased.Value;
            }
        }

        string canonical = CanonicalFieldName(name);
        if (canonical.Length == 0)
            return null;
        var matches = obj.Properties()
            .Where(p => CanonicalFieldName(p.Name) == canonical)
            .Take(2)
            .ToList();
        if (matches.Count == 1)
        {
            if (report != null)
                report.NormalizedNameMatches++;
            return matches[0].Value;
        }
        return null;
    }

    private static JToken? FindUniqueJsonDescendant(
        JToken token,
        string name,
        UABEvolCompatibleAssetImportReport? report = null)
    {
        var matches = new List<JToken>(2);
        CollectJsonDescendants(token, name, matches, 2, canonical: false);
        if (matches.Count == 1)
            return matches[0];
        if (matches.Count > 1)
            return null;

        matches.Clear();
        CollectJsonDescendants(token, name, matches, 2, canonical: true);
        if (matches.Count == 1)
        {
            if (report != null)
                report.NormalizedNameMatches++;
            return matches[0];
        }
        return null;
    }

    private static void CollectJsonDescendants(
        JToken token,
        string name,
        List<JToken> matches,
        int limit,
        bool canonical)
    {
        if (matches.Count >= limit)
            return;

        string canonicalName = canonical ? CanonicalFieldName(name) : string.Empty;
        if (token is JObject obj)
        {
            foreach (JProperty prop in obj.Properties())
            {
                bool same = canonical
                    ? canonicalName.Length != 0 && CanonicalFieldName(prop.Name) == canonicalName
                    : string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase);
                if (same)
                {
                    matches.Add(prop.Value);
                    if (matches.Count >= limit)
                        return;
                }
                CollectJsonDescendants(prop.Value, name, matches, limit, canonical);
                if (matches.Count >= limit)
                    return;
            }
        }
        else if (token is JArray array)
        {
            foreach (JToken item in array)
            {
                CollectJsonDescendants(item, name, matches, limit, canonical);
                if (matches.Count >= limit)
                    return;
            }
        }
    }

    private static bool JsonValueRequiredCoercion(AssetValueType targetType, JToken source)
    {
        return targetType switch
        {
            AssetValueType.Bool => source.Type != JTokenType.Boolean,
            AssetValueType.Int8 or AssetValueType.UInt8 or
            AssetValueType.Int16 or AssetValueType.UInt16 or
            AssetValueType.Int32 or AssetValueType.UInt32 or
            AssetValueType.Int64 or AssetValueType.UInt64 => source.Type != JTokenType.Integer,
            AssetValueType.Float or AssetValueType.Double =>
                source.Type != JTokenType.Float && source.Type != JTokenType.Integer,
            AssetValueType.String =>
                source.Type != JTokenType.String && source.Type != JTokenType.Null,
            AssetValueType.ByteArray => source.Type != JTokenType.Array,
            _ => false
        };
    }

    private static bool TryAssignJsonValue(AssetTypeValueField target, JToken source)
    {
        try
        {
            switch (target.TemplateField.ValueType)
            {
                case AssetValueType.Bool: target.AsBool = ConvertToBool(source); return true;
                case AssetValueType.Int8: target.AsSByte = checked((sbyte)ConvertToInt64(source)); return true;
                case AssetValueType.UInt8: target.AsByte = checked((byte)ConvertToUInt64(source)); return true;
                case AssetValueType.Int16: target.AsShort = checked((short)ConvertToInt64(source)); return true;
                case AssetValueType.UInt16: target.AsUShort = checked((ushort)ConvertToUInt64(source)); return true;
                case AssetValueType.Int32: target.AsInt = checked((int)ConvertToInt64(source)); return true;
                case AssetValueType.UInt32: target.AsUInt = checked((uint)ConvertToUInt64(source)); return true;
                case AssetValueType.Int64: target.AsLong = ConvertToInt64(source); return true;
                case AssetValueType.UInt64: target.AsULong = ConvertToUInt64(source); return true;
                case AssetValueType.Float: target.AsFloat = ConvertToSingle(source); return true;
                case AssetValueType.Double: target.AsDouble = ConvertToDouble(source); return true;
                case AssetValueType.String: target.AsString = source.Type == JTokenType.Null ? string.Empty : source.ToString(); return true;
                case AssetValueType.ByteArray:
                    if (source is JArray arr)
                    {
                        target.AsByteArray = arr.Select(ConvertToByte).ToArray();
                        return true;
                    }
                    return false;
                case AssetValueType.ManagedReferencesRegistry:
                    // Keep old registry in fallback mode. Exact-layout JSON is handled
                    // by AssetImport before this code path.
                    return false;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyTextNode(
        AssetTypeValueField target,
        DumpNode source,
        UABEvolCompatibleAssetImportReport report,
        bool allowUniqueDescendantFallback)
    {
        if (target.TemplateField.IsArray)
        {
            if (!source.IsArray)
            {
                DumpNode? nestedArray = FindDirectTextChild(source, target.FieldName, report);
                if (nestedArray == null || !nestedArray.IsArray)
                {
                    report.MissingTargetFields++;
                    return;
                }
                source = nestedArray;
            }

            List<DumpNode> elements = source.Children.Where(c => c.IsArrayIndex).ToList();
            if (target.TemplateField.ValueType == AssetValueType.ByteArray)
            {
                var bytes = new List<byte>(elements.Count);
                foreach (DumpNode element in elements)
                {
                    DumpNode? valueNode = element.FirstValueDescendant();
                    if (valueNode?.ValueText == null || !TryConvertTextToByte(valueNode.ValueText, out byte value))
                        continue;
                    bytes.Add(value);
                }
                target.AsByteArray = bytes.ToArray();
                report.ReplacedValues += bytes.Count;
                report.RebuiltArrays++;
                return;
            }

            var newChildren = new List<AssetTypeValueField>(elements.Count);
            for (int i = 0; i < elements.Count; i++)
            {
                AssetTypeValueField elementTarget = ValueBuilder.DefaultValueFieldFromArrayTemplate(target);

                DumpNode elementSource = elements[i].Children.Count == 1
                    ? elements[i].Children[0]
                    : elements[i];
                ApplyTextNode(elementTarget, elementSource, report, allowUniqueDescendantFallback: false);
                newChildren.Add(elementTarget);
            }
            target.Children = newChildren;
            target.AsArray = new AssetTypeArrayInfo(newChildren.Count);
            report.RebuiltArrays++;
            return;
        }

        if (IsLeaf(target))
        {
            string? text = source.ValueText;
            if (text == null && source.Children.Count == 1)
                text = source.Children[0].ValueText;
            if (text != null && TryAssignTextValue(target, text))
            {
                report.ReplacedValues++;
                AssetValueType sourceType = AssetTypeValueField.GetValueTypeByTypeName(source.Type);
                if (sourceType != AssetValueType.None && sourceType != target.TemplateField.ValueType)
                    report.ConvertedTypeValues++;
            }
            else
            {
                report.MissingTargetFields++;
            }
            return;
        }

        // If the dump root is a wrapper with the target's own name, operate on it.
        if (!source.IsSynthetic && !string.Equals(source.Name, target.FieldName, StringComparison.OrdinalIgnoreCase))
        {
            DumpNode? same = FindDirectTextChild(source, target.FieldName, report);
            if (same != null)
                source = same;
        }

        foreach (AssetTypeValueField child in target.Children)
        {
            DumpNode? childSource = FindDirectTextChild(source, child.FieldName, report);
            if (childSource == null && allowUniqueDescendantFallback)
                childSource = FindUniqueTextDescendant(source, child.FieldName, report);

            if (childSource == null)
            {
                if (!IsLeaf(child) && !child.TemplateField.IsArray)
                {
                    var bridgeReport = new UABEvolCompatibleAssetImportReport { UsedValueReplacement = true };
                    ApplyTextNode(child, source, bridgeReport, allowUniqueDescendantFallback: true);
                    if (ReportHasDataChanges(bridgeReport))
                    {
                        MergeReport(report, bridgeReport);
                        continue;
                    }
                }

                report.MissingTargetFields += CountLeafFields(child);
                continue;
            }

            ApplyTextNode(child, childSource, report, allowUniqueDescendantFallback);
        }
    }

    private static bool TryAssignTextValue(AssetTypeValueField target, string raw)
    {
        string value = raw.Trim();
        if (target.TemplateField.ValueType == AssetValueType.String)
        {
            target.AsString = ParseQuotedText(value);
            return true;
        }

        try
        {
            switch (target.TemplateField.ValueType)
            {
                case AssetValueType.Bool:
                    if (bool.TryParse(value, out bool b)) { target.AsBool = b; return true; }
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bi)) { target.AsBool = bi != 0; return true; }
                    return false;
                case AssetValueType.Int8: target.AsSByte = sbyte.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture); return true;
                case AssetValueType.UInt8: target.AsByte = byte.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture); return true;
                case AssetValueType.Int16: target.AsShort = short.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture); return true;
                case AssetValueType.UInt16: target.AsUShort = ushort.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture); return true;
                case AssetValueType.Int32: target.AsInt = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture); return true;
                case AssetValueType.UInt32: target.AsUInt = uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture); return true;
                case AssetValueType.Int64: target.AsLong = long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture); return true;
                case AssetValueType.UInt64: target.AsULong = ulong.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture); return true;
                case AssetValueType.Float: target.AsFloat = float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture); return true;
                case AssetValueType.Double: target.AsDouble = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture); return true;
                default: return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static DumpNode ParseTextDump(string text)
    {
        var root = DumpNode.SyntheticRoot();
        var stack = new Stack<(int Depth, DumpNode Node)>();
        stack.Push((-1, root));

        using var sr = new StringReader(text);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            int depth = 0;
            while (depth < line.Length && line[depth] == ' ')
                depth++;
            string content = line.Substring(depth).TrimEnd();
            if (content.Length == 0)
                continue;

            DumpNode node;
            if (content.StartsWith("[", StringComparison.Ordinal) && content.EndsWith("]", StringComparison.Ordinal))
            {
                node = new DumpNode(content, "index") { IsArrayIndex = true };
            }
            else
            {
                node = ParseTextFieldLine(content);
            }

            while (stack.Count > 1 && stack.Peek().Depth >= depth)
                stack.Pop();

            stack.Peek().Node.Children.Add(node);
            stack.Push((depth, node));
        }

        return root;
    }

    private static DumpNode ParseTextFieldLine(string content)
    {
        if (content.Length < 3 || (content[0] != '0' && content[0] != '1') || content[1] != ' ')
            return new DumpNode(content, string.Empty);

        string rest = content.Substring(2);
        int eq = rest.IndexOf(" = ", StringComparison.Ordinal);
        string left = eq >= 0 ? rest.Substring(0, eq) : rest;
        string? value = eq >= 0 ? rest.Substring(eq + 3) : null;

        bool isArray = Regex.IsMatch(left, @" \(\d+ items\)$", RegexOptions.CultureInvariant);
        if (isArray)
            left = Regex.Replace(left, @" \(\d+ items\)$", string.Empty, RegexOptions.CultureInvariant);

        int lastSpace = left.LastIndexOf(' ');
        string type = lastSpace >= 0 ? left.Substring(0, lastSpace) : string.Empty;
        string name = lastSpace >= 0 ? left.Substring(lastSpace + 1) : left;

        return new DumpNode(name, type)
        {
            IsArray = isArray,
            ValueText = value
        };
    }

    private static DumpNode SelectTextRoot(DumpNode root, string targetRootName)
    {
        if (root.Children.Count == 1)
            return root.Children[0];
        return FindDirectTextChild(root, targetRootName) ?? root;
    }

    private static bool IsTextShapeCompatible(AssetTypeValueField target, DumpNode source)
    {
        if (target.TemplateField.IsArray)
        {
            if (!source.IsArray)
                return false;
            if (target.TemplateField.ValueType == AssetValueType.ByteArray)
                return true;

            var elems = source.Children.Where(c => c.IsArrayIndex).ToList();
            if (elems.Count == 0)
                return true;

            DumpNode sample = elems[0].Children.Count == 1 ? elems[0].Children[0] : elems[0];
            AssetTypeValueField targetSample = target.Children.Count > 0
                ? target.Children[0]
                : ValueBuilder.DefaultValueFieldFromArrayTemplate(target);
            return IsTextShapeCompatible(targetSample, sample);
        }

        if (IsLeaf(target))
        {
            if (source.ValueText == null)
                return false;

            // Same field name and byte width is not enough. For example an int and
            // float are both four bytes but direct TXT import would reinterpret the
            // bits. Only use UABE's direct writer when the semantic leaf type matches;
            // otherwise force value conversion through the destination TypeTree.
            AssetValueType sourceType = AssetTypeValueField.GetValueTypeByTypeName(source.Type);
            return sourceType == target.TemplateField.ValueType;
        }

        List<DumpNode> sourceFields = source.Children
            .Where(c => !c.IsArrayIndex && !string.Equals(c.Name, "size", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sourceFields.Count != target.Children.Count)
            return false;

        for (int i = 0; i < target.Children.Count; i++)
        {
            if (!string.Equals(target.Children[i].FieldName, sourceFields[i].Name, StringComparison.Ordinal))
                return false;
            if (!IsTextShapeCompatible(target.Children[i], sourceFields[i]))
                return false;
        }
        return true;
    }

    private static DumpNode? FindDirectTextChild(
        DumpNode node,
        string name,
        UABEvolCompatibleAssetImportReport? report = null)
    {
        DumpNode? exact = node.Children.FirstOrDefault(c => !c.IsArrayIndex && c.Name == name);
        if (exact != null)
            return exact;

        DumpNode? caseInsensitive = node.Children.FirstOrDefault(c => !c.IsArrayIndex &&
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (caseInsensitive != null)
            return caseInsensitive;

        foreach (string alias in GetFieldAliases(name))
        {
            DumpNode? aliased = node.Children.FirstOrDefault(c => !c.IsArrayIndex &&
                string.Equals(c.Name, alias, StringComparison.OrdinalIgnoreCase));
            if (aliased != null)
            {
                if (report != null)
                    report.SemanticMigrations++;
                return aliased;
            }
        }

        string canonical = CanonicalFieldName(name);
        var matches = node.Children
            .Where(c => !c.IsArrayIndex && canonical.Length != 0 && CanonicalFieldName(c.Name) == canonical)
            .Take(2)
            .ToList();
        if (matches.Count == 1)
        {
            if (report != null)
                report.NormalizedNameMatches++;
            return matches[0];
        }
        return null;
    }

    private static DumpNode? FindUniqueTextDescendant(
        DumpNode node,
        string name,
        UABEvolCompatibleAssetImportReport? report = null)
    {
        var matches = new List<DumpNode>(2);
        CollectTextDescendants(node, name, matches, 2, canonical: false);
        if (matches.Count == 1)
            return matches[0];
        if (matches.Count > 1)
            return null;

        matches.Clear();
        CollectTextDescendants(node, name, matches, 2, canonical: true);
        if (matches.Count == 1)
        {
            if (report != null)
                report.NormalizedNameMatches++;
            return matches[0];
        }
        return null;
    }

    private static void CollectTextDescendants(
        DumpNode node,
        string name,
        List<DumpNode> matches,
        int limit,
        bool canonical)
    {
        string canonicalName = canonical ? CanonicalFieldName(name) : string.Empty;
        foreach (DumpNode child in node.Children)
        {
            if (child.IsArrayIndex)
            {
                CollectTextDescendants(child, name, matches, limit, canonical);
                if (matches.Count >= limit) return;
                continue;
            }

            bool same = canonical
                ? canonicalName.Length != 0 && CanonicalFieldName(child.Name) == canonicalName
                : string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase);
            if (same)
            {
                matches.Add(child);
                if (matches.Count >= limit)
                    return;
            }
            CollectTextDescendants(child, name, matches, limit, canonical);
            if (matches.Count >= limit)
                return;
        }
    }

    private static IEnumerable<string> GetFieldAliases(string targetName)
    {
        // Unity occasionally renames a field while preserving the same semantic value.
        // These aliases are deliberately narrow; they are only used after exact and
        // case-insensitive matching fails.
        switch (targetName)
        {
            case "m_KeepAnimatorStateOnDisable":
                yield return "m_KeepAnimatorControllerStateOnDisable";
                break;
            case "m_KeepAnimatorControllerStateOnDisable":
                yield return "m_KeepAnimatorStateOnDisable";
                break;
            case "emitterVelocityMode":
                yield return "useRigidbodyForVelocity";
                break;
            case "useRigidbodyForVelocity":
                yield return "emitterVelocityMode";
                break;
            case "rowMode":
                yield return "randomRow";
                break;
            case "randomRow":
                yield return "rowMode";
                break;
        }
    }

    private void ApplyKnownValueSemanticMigrations(
        AssetTypeValueField target,
        AssetTypeValueField originalTarget,
        AssetTypeValueField source,
        UABEvolCompatibleAssetImportReport report)
    {
        if (_targetTypeId == (int)AssetClassID.Shader)
        {
            // Unity changed Shader program-table serialization in 2019.3. Legacy
            // layouts store offsets/compressedLengths/decompressedLengths as flat
            // uint arrays, while newer layouts store one uint array per platform.
            // Generic array mapping cannot safely bridge those shapes: a scalar
            // target element receives an array source element and silently stays 0.
            // Rebuild the TARGET table explicitly, flattening only singleton rows.
            ApplyShaderProgramTableMigration(target, source, report);
            return;
        }

        if (_targetTypeId != (int)AssetClassID.Material)
            return;

        ApplyMaterialKeywordMigration(
            target,
            originalTarget,
            TryGetValueString(source, "m_ShaderKeywords"),
            GetValueStringVector(source, "m_ValidKeywords"),
            GetValueStringVector(source, "m_InvalidKeywords"),
            report);
    }

    private static void ApplyShaderProgramTableMigration(
        AssetTypeValueField target,
        AssetTypeValueField source,
        UABEvolCompatibleAssetImportReport report)
    {
        string[] tableNames = ["offsets", "compressedLengths", "decompressedLengths"];
        foreach (string tableName in tableNames)
        {
            AssetTypeValueField targetArray = GetNamedArrayNode(target, tableName);
            AssetTypeValueField sourceArray = GetNamedArrayNode(source, tableName);
            if (targetArray.IsDummy || sourceArray.IsDummy)
                continue;

            int targetRank = GetUnsignedArrayRank(targetArray);
            int sourceRank = GetUnsignedArrayRank(sourceArray);
            if (targetRank == 0 || sourceRank == 0 || targetRank == sourceRank)
                continue;

            if (!TryReadUnsignedRows(sourceArray, sourceRank, out List<List<uint>> rows))
            {
                throw new InvalidDataException(
                    $"Shader {tableName}: không đọc được bảng program nguồn để chuyển layout {sourceRank}D -> {targetRank}D.");
            }

            if (targetRank == 1 && rows.Any(row => row.Count != 1))
            {
                int widest = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
                throw new InvalidDataException(
                    $"Shader {tableName}: donor dùng multi-segment program table (max {widest} segment/platform); " +
                    "không thể hạ an toàn xuống layout legacy 1D.");
            }

            RebuildUnsignedRows(targetArray, targetRank, rows);
            report.RebuiltArrays++;
            report.ReplacedValues += rows.Sum(row => row.Count);
            report.SemanticMigrations++;
        }
    }

    private static void ApplyShaderProgramTableMigrationFromJson(
        AssetTypeValueField target,
        JToken source,
        UABEvolCompatibleAssetImportReport report)
    {
        foreach (string tableName in new[] { "offsets", "compressedLengths", "decompressedLengths" })
        {
            AssetTypeValueField targetArray = GetNamedArrayNode(target, tableName);
            JToken? sourceToken = FindJsonFieldForMigration(source, tableName);
            if (targetArray.IsDummy || sourceToken == null)
                continue;

            if (!TryReadUnsignedRowsFromJson(sourceToken, out List<List<uint>> rows, out int sourceRank))
                throw new InvalidDataException($"Shader JSON {tableName}: không đọc được bảng program nguồn.");

            int targetRank = GetUnsignedArrayRank(targetArray);
            if (targetRank == 0)
                continue;
            if (targetRank == 1 && rows.Any(row => row.Count != 1))
            {
                int widest = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
                throw new InvalidDataException(
                    $"Shader JSON {tableName}: donor có multi-segment program table (max {widest}/platform); " +
                    "không thể hạ an toàn về legacy 1D.");
            }

            RebuildUnsignedRows(targetArray, targetRank, rows);
            report.RebuiltArrays++;
            report.ReplacedValues += rows.Sum(row => row.Count);
            if (sourceRank != targetRank)
                report.SemanticMigrations++;
        }
    }

    private static bool TryReadUnsignedRowsFromJson(
        JToken token,
        out List<List<uint>> rows,
        out int rank)
    {
        rows = [];
        rank = 0;
        token = UnwrapJsonArrayToken(token);
        if (token is not JArray outer)
            return false;

        if (outer.Count == 0)
        {
            rank = 1;
            return true;
        }

        bool nested = outer.Any(item => UnwrapJsonArrayToken(item) is JArray);
        if (!nested)
        {
            rank = 1;
            foreach (JToken item in outer)
            {
                if (!TryReadJsonUInt32(item, out uint value))
                    return false;
                rows.Add([value]);
            }
            return true;
        }

        rank = 2;
        foreach (JToken item in outer)
        {
            JToken innerToken = UnwrapJsonArrayToken(item);
            if (innerToken is not JArray inner)
                return false;
            var row = new List<uint>(inner.Count);
            foreach (JToken valueToken in inner)
            {
                if (!TryReadJsonUInt32(valueToken, out uint value))
                    return false;
                row.Add(value);
            }
            rows.Add(row);
        }
        return true;
    }

    private static JToken UnwrapJsonArrayToken(JToken token)
    {
        JToken current = token;
        for (int i = 0; i < 4 && current is JObject obj; i++)
        {
            JProperty? array = obj.Properties().FirstOrDefault(p =>
                string.Equals(p.Name, "Array", StringComparison.OrdinalIgnoreCase));
            if (array != null)
            {
                current = array.Value;
                continue;
            }

            // Array element wrappers in some dump variants expose the scalar/array as
            // data/value. Do not unwrap arbitrary multi-field structs.
            JProperty[] props = obj.Properties().ToArray();
            if (props.Length == 1 &&
                (string.Equals(props[0].Name, "data", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(props[0].Name, "value", StringComparison.OrdinalIgnoreCase)))
            {
                current = props[0].Value;
                continue;
            }
            break;
        }
        return current;
    }

    private static bool TryReadJsonUInt32(JToken token, out uint value)
    {
        value = 0;
        token = UnwrapJsonArrayToken(token);
        try
        {
            if (token.Type == JTokenType.Integer)
            {
                long signed = token.Value<long>();
                if (signed < 0 || (ulong)signed > uint.MaxValue)
                    return false;
                value = (uint)signed;
                return true;
            }
            if (token.Type == JTokenType.String)
                return uint.TryParse(token.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyShaderProgramTableMigrationFromText(
        AssetTypeValueField target,
        DumpNode source,
        UABEvolCompatibleAssetImportReport report)
    {
        foreach (string tableName in new[] { "offsets", "compressedLengths", "decompressedLengths" })
        {
            AssetTypeValueField targetArray = GetNamedArrayNode(target, tableName);
            DumpNode? sourceNode = FindTextFieldForMigration(source, tableName);
            if (targetArray.IsDummy || sourceNode == null)
                continue;

            if (!TryReadUnsignedRowsFromText(sourceNode, out List<List<uint>> rows, out int sourceRank))
                throw new InvalidDataException($"Shader TXT {tableName}: không đọc được bảng program nguồn.");

            int targetRank = GetUnsignedArrayRank(targetArray);
            if (targetRank == 0)
                continue;
            if (targetRank == 1 && rows.Any(row => row.Count != 1))
            {
                int widest = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
                throw new InvalidDataException(
                    $"Shader TXT {tableName}: donor có multi-segment program table (max {widest}/platform); " +
                    "không thể hạ an toàn về legacy 1D.");
            }

            RebuildUnsignedRows(targetArray, targetRank, rows);
            report.RebuiltArrays++;
            report.ReplacedValues += rows.Sum(row => row.Count);
            if (sourceRank != targetRank)
                report.SemanticMigrations++;
        }
    }

    private static bool TryReadUnsignedRowsFromText(
        DumpNode source,
        out List<List<uint>> rows,
        out int rank)
    {
        rows = [];
        rank = 0;
        DumpNode? array = FindFirstTextArrayNode(source);
        if (array == null)
            return false;

        List<DumpNode> outerElements = array.Children.Where(c => c.IsArrayIndex).ToList();
        if (outerElements.Count == 0)
        {
            rank = 1;
            return true;
        }

        bool nested = outerElements.Any(element => FindFirstTextArrayNode(element) != null);
        if (!nested)
        {
            rank = 1;
            foreach (DumpNode element in outerElements)
            {
                DumpNode? valueNode = element.FirstValueDescendant();
                if (valueNode?.ValueText == null || !TryReadTextUInt32(valueNode.ValueText, out uint value))
                    return false;
                rows.Add([value]);
            }
            return true;
        }

        rank = 2;
        foreach (DumpNode element in outerElements)
        {
            DumpNode? innerArray = FindFirstTextArrayNode(element);
            if (innerArray == null)
                return false;
            var row = new List<uint>();
            foreach (DumpNode innerElement in innerArray.Children.Where(c => c.IsArrayIndex))
            {
                DumpNode? valueNode = innerElement.FirstValueDescendant();
                if (valueNode?.ValueText == null || !TryReadTextUInt32(valueNode.ValueText, out uint value))
                    return false;
                row.Add(value);
            }
            rows.Add(row);
        }
        return true;
    }

    private static DumpNode? FindFirstTextArrayNode(DumpNode node)
    {
        if (node.IsArray)
            return node;
        foreach (DumpNode child in node.Children)
        {
            if (child.IsArray)
                return child;
        }
        foreach (DumpNode child in node.Children)
        {
            DumpNode? nested = FindFirstTextArrayNode(child);
            if (nested != null)
                return nested;
        }
        return null;
    }

    private static bool TryReadTextUInt32(string text, out uint value)
    {
        value = 0;
        string raw = text.Trim().Trim('"');
        return uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private void FinalizeMonoBehaviourRawImportIfNeeded(
        AssetTypeValueField merged,
        AssetTypeValueField originalTarget,
        UABEvolCompatibleAssetImportReport report)
    {
        if (!IsMonoBehaviourTarget)
            return;

        // V27.1: the only PPtr preserved from the destination is m_Script.
        // This PPtr identifies the destination MonoScript/TypeTree. Every other
        // PPtr, including m_GameObject and references nested in UMA mesh data,
        // must retain the values imported from the RAW source.
        AssetTypeValueField originalScript = GetDirectChild(originalTarget, "m_Script");
        AssetTypeValueField importedScript = GetDirectChild(merged, "m_Script");
        if (originalScript.IsDummy || importedScript.IsDummy ||
            GetDirectChild(originalScript, "m_FileID").IsDummy ||
            GetDirectChild(originalScript, "m_PathID").IsDummy ||
            GetDirectChild(importedScript, "m_FileID").IsDummy ||
            GetDirectChild(importedScript, "m_PathID").IsDummy)
        {
            throw new InvalidDataException(
                "MonoBehaviour RAW: cannot preserve the destination m_Script PPtr " +
                "because its m_FileID/m_PathID fields are missing. Import cancelled.");
        }

        if (!ReplaceDirectChild(merged, "m_Script", originalScript))
            throw new InvalidDataException("MonoBehaviour RAW: failed to preserve the destination m_Script PPtr.");
        report.MonoBehaviourTargetFieldsPreserved++;

        // AssetFileInfo.PathID belongs to the selected destination object,
        // not to the RAW payload. It is unchanged by this value replacement.
    }

    private void FinalizeShaderImportIfNeeded(
        AssetTypeValueField merged,
        AssetTypeValueField originalTarget,
        UABEvolCompatibleAssetImportReport report,
        string sourceLabel)
    {
        if (_targetTypeId != (int)AssetClassID.Shader)
            return;

        RestoreShaderTargetOwnedFields(merged, originalTarget, report);
        if (!ValidateShaderProgramLayout(merged, out string error, out _))
            throw new InvalidDataException($"Shader {sourceLabel}: program table/blob không hợp lệ sau đồng bộ TARGET schema: {error}");
        report.ShaderProgramTablesVerified++;
    }

    private static void RestoreShaderTargetOwnedFields(
        AssetTypeValueField merged,
        AssetTypeValueField originalTarget,
        UABEvolCompatibleAssetImportReport report)
    {
        foreach (string fieldName in ShaderTargetOwnedFields)
        {
            AssetTypeValueField source = GetShaderOwnedField(originalTarget, fieldName);
            if (source.IsDummy)
                continue;
            if (ReplaceShaderOwnedField(merged, fieldName, source))
                report.ShaderTargetFieldsPreserved++;
        }
    }

    private static AssetTypeValueField GetDirectChild(AssetTypeValueField root, string name)
    {
        if (root == null || root.IsDummy)
            return AssetTypeValueField.DUMMY_FIELD;
        foreach (AssetTypeValueField child in root.Children)
        {
            if (string.Equals(child.TemplateField?.Name, name, StringComparison.Ordinal))
                return child;
        }
        return AssetTypeValueField.DUMMY_FIELD;
    }

    private static AssetTypeValueField GetShaderOwnedField(AssetTypeValueField root, string name)
    {
        AssetTypeValueField direct = GetDirectChild(root, name);
        if (!direct.IsDummy)
            return direct;
        AssetTypeValueField parsed = GetDirectChild(root, "m_ParsedForm");
        return parsed.IsDummy ? AssetTypeValueField.DUMMY_FIELD : GetDirectChild(parsed, name);
    }

    private static bool ReplaceShaderOwnedField(
        AssetTypeValueField root,
        string name,
        AssetTypeValueField replacement)
    {
        if (ReplaceDirectChild(root, name, replacement))
            return true;
        AssetTypeValueField parsed = GetDirectChild(root, "m_ParsedForm");
        return !parsed.IsDummy && ReplaceDirectChild(parsed, name, replacement);
    }

    private static bool ReplaceDirectChild(
        AssetTypeValueField root,
        string name,
        AssetTypeValueField replacement)
    {
        if (root == null || root.IsDummy || replacement == null || replacement.IsDummy)
            return false;
        for (int i = 0; i < root.Children.Count; i++)
        {
            if (!string.Equals(root.Children[i].TemplateField?.Name, name, StringComparison.Ordinal))
                continue;
            // AssetsTools.NET in UABEA exposes Clone() on AssetTypeTemplateField,
            // not on AssetTypeValueField. The replacement comes exclusively from
            // originalTarget, and after this method the merged tree is only read
            // (shader validation / WriteToByteArray), never mutated. Reusing this
            // immutable value subtree preserves the original m_Script PPtr and
            // shader target-owned fields exactly, without a nonexistent Clone API.
            root.Children[i] = replacement;
            return true;
        }
        return false;
    }

    private static bool ValidateShaderProgramLayout(
        AssetTypeValueField root,
        out string error,
        out int programBlocks)
    {
        error = string.Empty;
        programBlocks = 0;

        AssetTypeValueField platforms = GetNamedArrayNode(root, "platforms");
        AssetTypeValueField offsets = GetNamedArrayNode(root, "offsets");
        AssetTypeValueField compressedLengths = GetNamedArrayNode(root, "compressedLengths");
        AssetTypeValueField decompressedLengths = GetNamedArrayNode(root, "decompressedLengths");
        AssetTypeValueField blobField = GetDirectChild(root, "compressedBlob");

        bool hasAny = !platforms.IsDummy || !offsets.IsDummy || !compressedLengths.IsDummy ||
                      !decompressedLengths.IsDummy || !blobField.IsDummy;
        if (!hasAny)
            return true; // old Shader layouts without compiled program table

        if (platforms.IsDummy || offsets.IsDummy || compressedLengths.IsDummy ||
            decompressedLengths.IsDummy || blobField.IsDummy)
        {
            error = "program fields are only partially present";
            return false;
        }

        int offsetsRank = GetUnsignedArrayRank(offsets);
        int compressedRank = GetUnsignedArrayRank(compressedLengths);
        int decompressedRank = GetUnsignedArrayRank(decompressedLengths);
        if (offsetsRank == 0 || compressedRank == 0 || decompressedRank == 0 ||
            offsetsRank != compressedRank || offsetsRank != decompressedRank)
        {
            error = $"table rank mismatch offsets={offsetsRank} compressed={compressedRank} decompressed={decompressedRank}";
            return false;
        }

        if (!TryReadUnsignedRows(offsets, offsetsRank, out List<List<uint>> offsetRows) ||
            !TryReadUnsignedRows(compressedLengths, compressedRank, out List<List<uint>> compressedRows) ||
            !TryReadUnsignedRows(decompressedLengths, decompressedRank, out List<List<uint>> decompressedRows))
        {
            error = "cannot read uint program tables";
            return false;
        }

        if (offsetRows.Count != compressedRows.Count || offsetRows.Count != decompressedRows.Count)
        {
            error = $"outer row count mismatch offsets={offsetRows.Count} compressed={compressedRows.Count} decompressed={decompressedRows.Count}";
            return false;
        }

        if (platforms.Children.Count != offsetRows.Count)
        {
            error = $"platform count {platforms.Children.Count} != program rows {offsetRows.Count}";
            return false;
        }

        if (!TryReadShaderBlob(blobField, out byte[] blob))
        {
            error = "compressedBlob is not a readable byte array";
            return false;
        }

        for (int row = 0; row < offsetRows.Count; row++)
        {
            if (offsetRows[row].Count != compressedRows[row].Count ||
                offsetRows[row].Count != decompressedRows[row].Count)
            {
                error = $"row {row} segment count mismatch offsets={offsetRows[row].Count} compressed={compressedRows[row].Count} decompressed={decompressedRows[row].Count}";
                return false;
            }

            for (int segment = 0; segment < offsetRows[row].Count; segment++)
            {
                uint offset = offsetRows[row][segment];
                uint compressed = compressedRows[row][segment];
                uint decompressed = decompressedRows[row][segment];
                ulong end = (ulong)offset + compressed;
                if (end > (ulong)blob.Length)
                {
                    error = $"row {row} segment {segment} range {offset}+{compressed} exceeds blob {blob.Length}";
                    return false;
                }
                if (compressed == 0 && decompressed != 0)
                {
                    error = $"row {row} segment {segment} has compressedLength=0 but decompressedLength={decompressed}";
                    return false;
                }
                programBlocks++;
            }
        }

        return true;
    }

    private static bool TryReadShaderBlob(AssetTypeValueField blobField, out byte[] blob)
    {
        return TryExtractBytePayload(blobField, out blob);
    }

    /// <summary>
    /// Read byte vectors without trusting an empty AsByteArray as authoritative.
    /// AssetsTools may materialize the same Unity vector&lt;UInt8&gt; as numeric children,
    /// especially for Shader compressedBlob fields loaded through an external ClassDB.
    /// </summary>
    private static bool TryExtractBytePayload(AssetTypeValueField field, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (field == null || field.IsDummy)
            return false;

        bool readableEmptyDirect = false;
        try
        {
            byte[]? direct = field.AsByteArray;
            if (direct != null)
            {
                if (direct.Length > 0)
                {
                    bytes = (byte[])direct.Clone();
                    return true;
                }
                readableEmptyDirect = true;
            }
        }
        catch { }

        AssetTypeValueField array = field.TemplateField.IsArray
            ? field
            : GetDirectChild(field, "Array");
        if (!array.IsDummy)
        {
            try
            {
                byte[]? nested = array.AsByteArray;
                if (nested != null)
                {
                    if (nested.Length > 0)
                    {
                        bytes = (byte[])nested.Clone();
                        return true;
                    }
                    readableEmptyDirect = true;
                }
            }
            catch { }

            try
            {
                if (array.Children.Count > 0)
                {
                    var packed = new byte[array.Children.Count];
                    for (int i = 0; i < array.Children.Count; i++)
                    {
                        if (!TryReadUInt32(array.Children[i], out uint value) || value > byte.MaxValue)
                            return false;
                        packed[i] = (byte)value;
                    }
                    bytes = packed;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        if (readableEmptyDirect)
        {
            bytes = Array.Empty<byte>();
            return true;
        }
        return false;
    }

    private static AssetTypeValueField GetNamedArrayNode(AssetTypeValueField scope, string name)
    {
        AssetTypeValueField field = scope[name];
        if (field.IsDummy)
            return AssetTypeValueField.DUMMY_FIELD;
        if (field.TemplateField.IsArray)
            return field;

        AssetTypeValueField array = field["Array"];
        if (!array.IsDummy && array.TemplateField.IsArray)
            return array;

        foreach (AssetTypeValueField child in field.Children)
        {
            if (child.TemplateField.IsArray)
                return child;
        }
        return AssetTypeValueField.DUMMY_FIELD;
    }

    private static AssetTypeValueField GetNestedArrayNode(AssetTypeValueField field)
    {
        if (field.IsDummy)
            return AssetTypeValueField.DUMMY_FIELD;
        if (field.TemplateField.IsArray)
            return field;

        AssetTypeValueField array = field["Array"];
        if (!array.IsDummy && array.TemplateField.IsArray)
            return array;

        foreach (AssetTypeValueField child in field.Children)
        {
            if (child.TemplateField.IsArray)
                return child;
        }
        return AssetTypeValueField.DUMMY_FIELD;
    }

    private static int GetUnsignedArrayRank(AssetTypeValueField array)
    {
        if (array.IsDummy || !array.TemplateField.IsArray)
            return 0;

        AssetTypeValueField? element = array.Children.Count > 0
            ? array.Children[0]
            : ValueBuilder.DefaultValueFieldFromArrayTemplate(array);
        if (element == null || element.IsDummy)
            return 0;

        return GetNestedArrayNode(element).IsDummy ? 1 : 2;
    }

    private static bool TryReadUnsignedRows(
        AssetTypeValueField array,
        int rank,
        out List<List<uint>> rows)
    {
        rows = new List<List<uint>>(array.Children.Count);
        if (rank == 1)
        {
            foreach (AssetTypeValueField element in array.Children)
            {
                if (!TryReadUInt32(element, out uint value))
                    return false;
                rows.Add([value]);
            }
            return true;
        }

        if (rank != 2)
            return false;

        foreach (AssetTypeValueField outerElement in array.Children)
        {
            AssetTypeValueField inner = GetNestedArrayNode(outerElement);
            if (inner.IsDummy)
                return false;

            var row = new List<uint>(inner.Children.Count);
            foreach (AssetTypeValueField valueField in inner.Children)
            {
                if (!TryReadUInt32(valueField, out uint value))
                    return false;
                row.Add(value);
            }
            rows.Add(row);
        }
        return true;
    }

    private static void RebuildUnsignedRows(
        AssetTypeValueField targetArray,
        int targetRank,
        IReadOnlyList<List<uint>> rows)
    {
        var outerChildren = new List<AssetTypeValueField>(rows.Count);
        foreach (IReadOnlyList<uint> row in rows)
        {
            AssetTypeValueField outerElement = ValueBuilder.DefaultValueFieldFromArrayTemplate(targetArray)
                ?? throw new InvalidDataException("Shader program table: TARGET array template không tạo được element.");

            if (targetRank == 1)
            {
                if (row.Count != 1)
                    throw new InvalidDataException("Shader program table legacy yêu cầu đúng 1 value/platform.");
                WriteUInt32(outerElement, row[0]);
                outerChildren.Add(outerElement);
                continue;
            }

            AssetTypeValueField inner = GetNestedArrayNode(outerElement);
            if (inner.IsDummy)
                throw new InvalidDataException("Shader program table 2D: TARGET element không có inner Array.");

            var innerChildren = new List<AssetTypeValueField>(row.Count);
            foreach (uint value in row)
            {
                AssetTypeValueField innerElement = ValueBuilder.DefaultValueFieldFromArrayTemplate(inner)
                    ?? throw new InvalidDataException("Shader program table 2D: TARGET inner template không tạo được element.");
                WriteUInt32(innerElement, value);
                innerChildren.Add(innerElement);
            }
            inner.Children = innerChildren;
            inner.AsArray = new AssetTypeArrayInfo(innerChildren.Count);
            outerChildren.Add(outerElement);
        }

        targetArray.Children = outerChildren;
        targetArray.AsArray = new AssetTypeArrayInfo(outerChildren.Count);
    }

    private static bool TryReadUInt32(AssetTypeValueField field, out uint value)
    {
        value = 0;
        if (field == null || field.IsDummy || field.Value == null)
            return false;
        try
        {
            switch (field.TemplateField.ValueType)
            {
                case AssetValueType.UInt8: value = field.AsByte; return true;
                case AssetValueType.UInt16: value = field.AsUShort; return true;
                case AssetValueType.UInt32: value = field.AsUInt; return true;
                case AssetValueType.UInt64:
                    if (field.AsULong > uint.MaxValue) return false;
                    value = (uint)field.AsULong; return true;
                case AssetValueType.Int8:
                    if (field.AsSByte < 0) return false;
                    value = (uint)field.AsSByte; return true;
                case AssetValueType.Int16:
                    if (field.AsShort < 0) return false;
                    value = (uint)field.AsShort; return true;
                case AssetValueType.Int32:
                    if (field.AsInt < 0) return false;
                    value = (uint)field.AsInt; return true;
                case AssetValueType.Int64:
                    if (field.AsLong < 0 || field.AsLong > uint.MaxValue) return false;
                    value = (uint)field.AsLong; return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void WriteUInt32(AssetTypeValueField field, uint value)
    {
        switch (field.TemplateField.ValueType)
        {
            case AssetValueType.UInt8: field.AsByte = checked((byte)value); break;
            case AssetValueType.UInt16: field.AsUShort = checked((ushort)value); break;
            case AssetValueType.UInt32: field.AsUInt = value; break;
            case AssetValueType.UInt64: field.AsULong = value; break;
            case AssetValueType.Int8: field.AsSByte = checked((sbyte)value); break;
            case AssetValueType.Int16: field.AsShort = checked((short)value); break;
            case AssetValueType.Int32: field.AsInt = checked((int)value); break;
            case AssetValueType.Int64: field.AsLong = value; break;
            default:
                throw new InvalidDataException(
                    $"Shader program table: kiểu scalar TARGET '{field.TemplateField.ValueType}' không hỗ trợ uint.");
        }
    }

    private void ApplyKnownJsonSemanticMigrations(
        AssetTypeValueField target,
        AssetTypeValueField originalTarget,
        JToken source,
        UABEvolCompatibleAssetImportReport report)
    {
        if (_targetTypeId == (int)AssetClassID.Shader)
        {
            ApplyShaderProgramTableMigrationFromJson(target, source, report);
            return;
        }

        if (_targetTypeId != (int)AssetClassID.Material)
            return;

        JToken? oldToken = FindJsonFieldForMigration(source, "m_ShaderKeywords");
        JToken? validToken = FindJsonFieldForMigration(source, "m_ValidKeywords");
        JToken? invalidToken = FindJsonFieldForMigration(source, "m_InvalidKeywords");

        string? oldKeywords = oldToken == null ? null : oldToken.Type == JTokenType.String
            ? oldToken.Value<string>()
            : oldToken.ToString();

        ApplyMaterialKeywordMigration(
            target,
            originalTarget,
            oldKeywords,
            ExtractJsonStringVector(validToken),
            ExtractJsonStringVector(invalidToken),
            report);
    }

    private void ApplyKnownTextSemanticMigrations(
        AssetTypeValueField target,
        AssetTypeValueField originalTarget,
        DumpNode source,
        UABEvolCompatibleAssetImportReport report)
    {
        if (_targetTypeId == (int)AssetClassID.Shader)
        {
            ApplyShaderProgramTableMigrationFromText(target, source, report);
            return;
        }

        if (_targetTypeId != (int)AssetClassID.Material)
            return;

        DumpNode? oldNode = FindTextFieldForMigration(source, "m_ShaderKeywords");
        DumpNode? validNode = FindTextFieldForMigration(source, "m_ValidKeywords");
        DumpNode? invalidNode = FindTextFieldForMigration(source, "m_InvalidKeywords");

        string? oldKeywords = oldNode?.ValueText == null
            ? null
            : ParseQuotedText(oldNode.ValueText.Trim());

        ApplyMaterialKeywordMigration(
            target,
            originalTarget,
            oldKeywords,
            ExtractTextStringVector(validNode),
            ExtractTextStringVector(invalidNode),
            report);
    }

    private static void ApplyMaterialKeywordMigration(
        AssetTypeValueField target,
        AssetTypeValueField originalTarget,
        string? legacyKeywordString,
        IReadOnlyList<string>? sourceValidKeywords,
        IReadOnlyList<string>? sourceInvalidKeywords,
        UABEvolCompatibleAssetImportReport report)
    {
        AssetTypeValueField legacyTarget = target["m_ShaderKeywords"];
        if (!legacyTarget.IsDummy)
        {
            var merged = new List<string>();
            AddUniqueKeywords(merged, sourceValidKeywords);
            AddUniqueKeywords(merged, sourceInvalidKeywords);
            if (merged.Count > 0)
            {
                legacyTarget.AsString = string.Join(" ", merged);
                report.ReplacedValues += merged.Count;
                report.MissingTargetFields = Math.Max(0, report.MissingTargetFields - 1);
                report.SemanticMigrations++;
            }
            return;
        }

        AssetTypeValueField validTarget = target["m_ValidKeywords"];
        AssetTypeValueField invalidTarget = target["m_InvalidKeywords"];
        if (validTarget.IsDummy || invalidTarget.IsDummy || string.IsNullOrWhiteSpace(legacyKeywordString))
            return;

        List<string> imported = SplitMaterialKeywords(legacyKeywordString);
        if (imported.Count == 0)
            return;

        // The old Material format only had one keyword string. New Unity versions
        // distinguish valid and invalid keywords. Use the current TARGET material only
        // as a classification hint; no target keyword that is absent from the import is
        // retained. Unknown imported keywords default to the valid list so enabled-keyword
        // semantics are preserved instead of silently dropping them.
        HashSet<string> targetInvalid = new HashSet<string>(
            GetValueStringVector(originalTarget, "m_InvalidKeywords"),
            StringComparer.Ordinal);

        var valid = new List<string>();
        var invalid = new List<string>();
        foreach (string keyword in imported)
        {
            if (targetInvalid.Contains(keyword))
                invalid.Add(keyword);
            else
                valid.Add(keyword);
        }

        SetValueStringVector(validTarget, valid);
        SetValueStringVector(invalidTarget, invalid);
        report.RebuiltArrays += 2;
        report.ReplacedValues += imported.Count;
        report.MissingTargetFields = Math.Max(0, report.MissingTargetFields - 2);
        report.SemanticMigrations++;
    }

    private static string? TryGetValueString(AssetTypeValueField scope, string name)
    {
        AssetTypeValueField field = scope[name];
        return field.IsDummy || field.TemplateField.ValueType != AssetValueType.String
            ? null
            : field.AsString;
    }

    private static List<string> GetValueStringVector(AssetTypeValueField scope, string name)
    {
        AssetTypeValueField vector = scope[name];
        if (vector.IsDummy)
            return new List<string>();

        AssetTypeValueField array = vector["Array"];
        if (array.IsDummy || !array.TemplateField.IsArray)
            return new List<string>();

        var values = new List<string>(array.Children.Count);
        foreach (AssetTypeValueField element in array.Children)
        {
            if (element.TemplateField.ValueType == AssetValueType.String)
                values.Add(element.AsString);
        }
        return values;
    }

    private static void SetValueStringVector(AssetTypeValueField vector, IReadOnlyList<string> values)
    {
        AssetTypeValueField array = vector["Array"];
        if (array.IsDummy || !array.TemplateField.IsArray)
            return;

        var children = new List<AssetTypeValueField>(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            AssetTypeValueField element = ValueBuilder.DefaultValueFieldFromArrayTemplate(array);
            element.AsString = values[i];
            children.Add(element);
        }
        array.Children = children;
        array.AsArray = new AssetTypeArrayInfo(children.Count);
    }

    private static List<string> SplitMaterialKeywords(string keywords)
    {
        return Regex.Split(keywords.Trim(), @"\s+")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static void AddUniqueKeywords(List<string> destination, IReadOnlyList<string>? source)
    {
        if (source == null)
            return;
        foreach (string value in source)
        {
            if (!string.IsNullOrWhiteSpace(value) && !destination.Contains(value, StringComparer.Ordinal))
                destination.Add(value);
        }
    }

    private static JToken? FindJsonFieldForMigration(JToken source, string name)
    {
        if (source is JObject obj)
        {
            JProperty? direct = obj.Properties().FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (direct != null)
                return direct.Value;
        }
        return FindUniqueJsonDescendant(source, name);
    }

    private static List<string> ExtractJsonStringVector(JToken? token)
    {
        if (token == null)
            return new List<string>();
        if (token is JValue scalar && scalar.Type == JTokenType.String)
            return SplitMaterialKeywords(scalar.Value<string>() ?? string.Empty);
        if (token is JObject obj)
        {
            JToken? arrayToken = obj.Properties().FirstOrDefault(p =>
                string.Equals(p.Name, "Array", StringComparison.OrdinalIgnoreCase))?.Value;
            if (arrayToken != null)
                return ExtractJsonStringVector(arrayToken);
        }
        if (token is JArray array)
        {
            var result = new List<string>();
            foreach (JToken item in array)
            {
                if (item.Type == JTokenType.String)
                    result.Add(item.ToString());
                else if (item is JObject itemObj)
                {
                    JToken? value = itemObj.Properties().FirstOrDefault(p =>
                        string.Equals(p.Name, "data", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.Name, "value", StringComparison.OrdinalIgnoreCase))?.Value;
                    if (value?.Type == JTokenType.String)
                        result.Add(value.ToString());
                }
            }
            return result.Distinct(StringComparer.Ordinal).ToList();
        }
        return new List<string>();
    }

    private static DumpNode? FindTextFieldForMigration(DumpNode source, string name)
    {
        DumpNode? direct = FindDirectTextChild(source, name);
        return direct ?? FindUniqueTextDescendant(source, name);
    }

    private static List<string> ExtractTextStringVector(DumpNode? node)
    {
        if (node == null)
            return new List<string>();

        DumpNode? array = node.IsArray ? node : node.Children.FirstOrDefault(c => c.IsArray);
        if (array == null)
            return new List<string>();

        var result = new List<string>();
        foreach (DumpNode element in array.Children.Where(c => c.IsArrayIndex))
        {
            DumpNode? value = element.FirstValueDescendant();
            if (value?.ValueText != null)
                result.Add(ParseQuotedText(value.ValueText.Trim()));
        }
        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    private static bool ReportHasDataChanges(UABEvolCompatibleAssetImportReport report)
        => report.ReplacedValues > 0 || report.RebuiltArrays > 0;

    private static void MergeReport(
        UABEvolCompatibleAssetImportReport target,
        UABEvolCompatibleAssetImportReport source)
    {
        target.ReplacedValues += source.ReplacedValues;
        target.RebuiltArrays += source.RebuiltArrays;
        target.MissingTargetFields += source.MissingTargetFields;
        target.IgnoredSourceFields += source.IgnoredSourceFields;
        target.ConvertedTypeValues += source.ConvertedTypeValues;
        target.NormalizedNameMatches += source.NormalizedNameMatches;
        target.SemanticMigrations += source.SemanticMigrations;
        target.RawTrailingZeroBytes = Math.Max(target.RawTrailingZeroBytes, source.RawTrailingZeroBytes);
        target.MonoBehaviourTargetFieldsPreserved += source.MonoBehaviourTargetFieldsPreserved;
    }

    private static void EnsureFallbackChangedSomething(UABEvolCompatibleAssetImportReport report, string format)
    {
        if (report.ReplacedValues == 0 && report.RebuiltArrays == 0)
        {
            throw new InvalidDataException(
                $"{format} source layout differs from the selected asset, but no compatible same-name node values were found. " +
                "The selected asset was left unchanged.");
        }
    }

    private static bool IsLeaf(AssetTypeValueField field)
    {
        return !field.TemplateField.IsArray &&
               field.TemplateField.ValueType != AssetValueType.None &&
               field.Children.Count == 0;
    }

    private static int CountLeafFields(AssetTypeValueField field)
    {
        if (field.TemplateField.IsArray || IsLeaf(field))
            return 1;
        int count = 0;
        foreach (AssetTypeValueField child in field.Children)
            count += CountLeafFields(child);
        return Math.Max(1, count);
    }

    private static byte ConvertToByte(JToken token) => checked((byte)ConvertToUInt64(token));

    private static bool ConvertToBool(JToken token)
    {
        if (token.Type == JTokenType.Boolean)
            return token.Value<bool>();
        string s = token.ToString();
        if (bool.TryParse(s, out bool b))
            return b;
        return ConvertToInt64(token) != 0;
    }

    private static long ConvertToInt64(JToken token)
    {
        if (token.Type == JTokenType.Boolean)
            return token.Value<bool>() ? 1L : 0L;
        if (token.Type == JTokenType.Integer)
            return token.Value<long>();
        if (token.Type == JTokenType.Float)
            return checked((long)token.Value<double>());
        return long.Parse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static ulong ConvertToUInt64(JToken token)
    {
        if (token.Type == JTokenType.Boolean)
            return token.Value<bool>() ? 1UL : 0UL;
        if (token.Type == JTokenType.Integer)
            return token.Value<ulong>();
        if (token.Type == JTokenType.Float)
            return checked((ulong)token.Value<double>());
        return ulong.Parse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static float ConvertToSingle(JToken token)
        => float.Parse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static double ConvertToDouble(JToken token)
        => double.Parse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static bool TryConvertTextToByte(string text, out byte value)
        => byte.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static string ParseQuotedText(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value.Substring(1, value.Length - 2);

        var sb = new StringBuilder(value.Length);
        bool escaping = false;
        foreach (char c in value)
        {
            if (!escaping && c == '\\')
            {
                escaping = true;
                continue;
            }
            if (escaping)
            {
                sb.Append(c switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => c
                });
                escaping = false;
            }
            else
            {
                sb.Append(c);
            }
        }
        if (escaping)
            sb.Append('\\');
        return sb.ToString();
    }

    private static string DecodeText(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var sr = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: false);
        return sr.ReadToEnd();
    }

    private sealed class DumpNode
    {
        public string Name { get; }
        public string Type { get; }
        public string? ValueText { get; set; }
        public bool IsArray { get; set; }
        public bool IsArrayIndex { get; set; }
        public bool IsSynthetic { get; private set; }
        public List<DumpNode> Children { get; } = [];

        public DumpNode(string name, string type)
        {
            Name = name;
            Type = type;
        }

        public static DumpNode SyntheticRoot()
            => new DumpNode("<root>", string.Empty) { IsSynthetic = true };

        public DumpNode? FirstValueDescendant()
        {
            if (ValueText != null)
                return this;
            foreach (DumpNode child in Children)
            {
                DumpNode? found = child.FirstValueDescendant();
                if (found != null)
                    return found;
            }
            return null;
        }
    }
}
