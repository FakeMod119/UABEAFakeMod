using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System;
using System.IO;

namespace UABEAvalonia;

/// <summary>
/// Routes the original UABEA Import Dump and Import Raw buttons through UABEvol's
/// target-TypeTree-compatible import logic. This is a single-object importer,
/// not Import Pro's recursive source-AssetBundle/PPtr dependency copier: a
/// stand-alone .dat/.txt/.json has no external-file identity to trace.
/// </summary>
public static class UABEvolImportBridge
{
    public static bool TryImport(
        AssetWorkspace workspace,
        AssetContainer destination,
        string sourcePath,
        UABEvolAssetImportSourceFormat format,
        out string diagnostic)
    {
        diagnostic = string.Empty;
        try
        {
            byte[] source = File.ReadAllBytes(sourcePath);
            if (source.Length == 0)
                throw new InvalidDataException("Import file is empty. No replacement was written.");

            AssetTypeValueField? target = workspace.GetBaseField(destination);
            byte[] output;
            if (target == null)
            {
                // Retail/type-tree-disabled files cannot always be deserialized.
                // Preserve UABEA's historic RAW fallback; never pretend that an
                // unknown layout has been checked or migrate JSON/TXT blindly.
                if (format != UABEvolAssetImportSourceFormat.Raw)
                    throw new InvalidDataException(
                        "Cannot import a typed dump: no target TypeTree is available. " +
                        "Load a matching class database/MonoScript schema first.");
                output = source;
                diagnostic = "RAW byte-for-byte fallback (no destination TypeTree; layout NOT verified).";
            }
            else
            {
                // MonoScript managed reference registries are required for fully
                // typed MonoBehaviour imports; GetRefTypeManager may be empty for
                // ordinary Unity classes and is not required for their scalars.
                RefTypeManager refTypes = (destination.ClassId == (int)AssetClassID.MonoBehaviour || destination.ClassId < 0
                    ? workspace.am.GetRefTypeManager(destination.FileInstance) : null)
                    ?? new RefTypeManager();
                var importer = new UABEvolCompatibleAssetImporter(
                    source,
                    refTypes,
                    destination.FileInstance.file.Header.Endianness,
                    workspace.am,
                    destination.ClassId);
                byte[]? imported = importer.Import(
                    target, format, out string? exceptionMessage,
                    out UABEvolCompatibleAssetImportReport report);
                if (imported == null)
                    throw new InvalidDataException(
                        $"{Path.GetFileName(sourcePath)}: {exceptionMessage ?? "unknown conversion error"}");
                if (imported.Length == 0)
                    throw new InvalidDataException("Converted asset is empty. Import aborted.");
                output = imported;
                diagnostic = report.ToString();
            }

            // Only commit after UABEvol validation/serialization succeeds. Replacer
            // identity (PathID/ClassID/MonoScriptID) stays from selected destination.
            AssetsReplacer replacer = AssetImportExport.CreateAssetReplacer(destination, output);
            workspace.AddReplacer(destination.FileInstance, replacer, new MemoryStream(output, writable: false));
            return true;
        }
        catch (Exception ex)
        {
            diagnostic = ex.Message;
            return false;
        }
    }
}
