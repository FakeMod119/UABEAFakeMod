using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace UABEAvalonia;

/// <summary>
/// Prepared asset bytes are NOT yet registered in the workspace. Prepare can run
/// off the UI thread; Commit must run on the Avalonia UI thread (AddReplacer
/// raises ItemUpdated and touches the DataGrid).
/// </summary>
public sealed class UABEvolPreparedImport
{
    public byte[]? Data { get; }
    public string Diagnostic { get; }
    public bool Success => Data != null;

    internal UABEvolPreparedImport(byte[]? data, string diagnostic)
    {
        Data = data;
        Diagnostic = diagnostic;
    }
}

/// <summary>
/// Single-asset UABEvol-compatible import. RAW/DAT/BIN does not encode its source
/// TypeTree, Unity version or external dependency identity. Compatibility checks
/// are best effort; large generic raw assets use explicit legacy byte replacement.
/// </summary>
public static class UABEvolImportBridge
{
    private const long MaxInputBytes = 512L * 1024 * 1024;
    private const int LargeGenericRawBytes = 8 * 1024 * 1024;

    public static UABEvolPreparedImport Prepare(
        AssetWorkspace workspace,
        AssetContainer destination,
        string sourcePath,
        UABEvolAssetImportSourceFormat format,
        CancellationToken cancellationToken = default)
    {
        Stopwatch timer = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(sourcePath);
            if (info.Length == 0)
                throw new InvalidDataException("Import file is empty. No replacement was written.");
            if (info.Length > MaxInputBytes)
                throw new InvalidDataException(
                    "Asset RAW exceeds 512 MiB. Import skipped to avoid excessive memory use; " +
                    "the original asset has not been changed.");

            byte[] source = File.ReadAllBytes(sourcePath);
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Length == 0)
                throw new InvalidDataException("Import file is empty. No replacement was written.");

            bool isMono = destination.ClassId == (int)AssetClassID.MonoBehaviour || destination.ClassId < 0;
            bool isShader = destination.ClassId == (int)AssetClassID.Shader;
            bool raw = format == UABEvolAssetImportSourceFormat.Raw;

            // AnimationClip is explicitly byte-for-byte in the upstream importer.
            // Large Texture2D/Mesh/AudioClip/etc. RAW is also a byte replacement:
            // decoding an entire object and scanning Unity history adds minutes,
            // high memory pressure and no trustworthy source-version metadata.
            if (raw && (destination.ClassId == (int)AssetClassID.AnimationClip ||
                        (source.Length >= LargeGenericRawBytes && !isMono && !isShader)))
            {
                string note = destination.ClassId == (int)AssetClassID.AnimationClip
                    ? "AnimationClip RAW byte-for-byte replacement (source schema NOT verified)."
                    : "Large RAW byte-for-byte replacement (source schema NOT verified; " +
                      "TypeTree migration skipped).";
                return new UABEvolPreparedImport(source, note + $" Prepared in {timer.ElapsedMilliseconds} ms.");
            }

            // R4: Material and UMA *_Mesh RAW are handled before GetBaseField.
            // GetBaseField and unrelated historical TypeTree probes can try to
            // deserialize enormous arrays from a mismatched schema, freezing UABEA.
            // Fast paths are bounded header checks + byte replacement, with
            // destination m_Shader/m_Script pointers protected and an explicit
            // NOT verified warning. Other classes keep the R3 typed importer.
            if (raw && UABEvolRawKnownAssetImport.TryPrepare(
                    destination, source, out byte[]? knownRaw, out string knownDiagnostic))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new UABEvolPreparedImport(knownRaw,
                    knownDiagnostic + $" Prepared in {timer.ElapsedMilliseconds} ms.");
            }

            AssetTypeValueField? target = workspace.GetBaseField(destination);
            if (target == null)
            {
                if (!raw)
                    throw new InvalidDataException(
                        "Cannot import typed dump: no destination TypeTree is available. " +
                        "Load the matching class database/MonoScript schema.");
                return new UABEvolPreparedImport(source,
                    "RAW byte-for-byte fallback (no destination TypeTree; layout NOT verified). " +
                    $"Prepared in {timer.ElapsedMilliseconds} ms.");
            }

            RefTypeManager refTypes = (isMono
                ? workspace.am.GetRefTypeManager(destination.FileInstance) : null)
                ?? new RefTypeManager();
            var importer = new UABEvolCompatibleAssetImporter(
                source,
                refTypes,
                destination.FileInstance.file.Header.Endianness,
                workspace.am,
                destination.ClassId,
                cancellationToken: cancellationToken);
            byte[]? output = importer.Import(target, format,
                out string? exceptionMessage, out UABEvolCompatibleAssetImportReport report);
            cancellationToken.ThrowIfCancellationRequested();
            if (output == null)
                throw new InvalidDataException(
                    $"{Path.GetFileName(sourcePath)}: {exceptionMessage ?? "unknown conversion error"}");
            if (output.Length == 0)
                throw new InvalidDataException("Converted asset is empty. Import aborted.");

            return new UABEvolPreparedImport(output,
                report + $" Prepared in {timer.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"UABEA import prepare failed: {ex}");
            return new UABEvolPreparedImport(null, ex.Message +
                $" (elapsed {timer.ElapsedMilliseconds} ms; destination unchanged)");
        }
    }

    // Commit MUST execute on the UI thread. Import is atomic per selected asset:
    // validation/serialization is complete before the workspace is mutated.
    public static bool TryCommit(
        AssetWorkspace workspace,
        AssetContainer destination,
        UABEvolPreparedImport prepared,
        out string diagnostic)
    {
        diagnostic = prepared.Diagnostic;
        if (prepared.Data == null)
            return false;
        try
        {
            AssetsReplacer replacer = AssetImportExport.CreateAssetReplacer(destination, prepared.Data);
            workspace.AddReplacer(destination.FileInstance, replacer,
                new MemoryStream(prepared.Data, writable: false));
            return true;
        }
        catch (Exception ex)
        {
            diagnostic = "Failed to commit import: " + ex.Message;
            Trace.WriteLine($"UABEA import commit failed: {ex}");
            return false;
        }
    }

    // Preserve the old API for plugins that call it synchronously. UI code must
    // use Prepare on a worker and TryCommit on the dispatcher instead.
    public static bool TryImport(
        AssetWorkspace workspace,
        AssetContainer destination,
        string sourcePath,
        UABEvolAssetImportSourceFormat format,
        out string diagnostic)
    {
        UABEvolPreparedImport prepared = Prepare(workspace, destination, sourcePath, format);
        return TryCommit(workspace, destination, prepared, out diagnostic);
    }
}
