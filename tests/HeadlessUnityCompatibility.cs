using System;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

internal static class HeadlessUnityCompatibility
{
    internal static string TextCoreAssembly(string path, string repoRoot)
    {
        // Unity 6.3's FontAsset has sequential layout over an auto-layout TextAsset base.
        // Mono accepts this; CoreCLR rejects the type even when only inspecting a menu.
        // No font objects are created by these tests. Change only that layout flag in
        // an ignored test-only copy; all method bodies and the game installation stay intact.
        var bytes = File.ReadAllBytes(path);
        using var pe = new PEReader(new MemoryStream(bytes, writable: false));
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            if (metadata.GetString(type.Namespace) != "UnityEngine.TextCore.Text"
                || metadata.GetString(type.Name) != "FontAsset") continue;
            if ((type.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.SequentialLayout) return path;
            var offset = pe.PEHeaders.MetadataStartOffset + metadata.GetTableMetadataOffset(TableIndex.TypeDef)
                + (MetadataTokens.GetRowNumber(handle) - 1) * metadata.GetTableRowSize(TableIndex.TypeDef);
            var attributes = (uint)(type.Attributes & ~TypeAttributes.LayoutMask);
            BitConverter.GetBytes(attributes).CopyTo(bytes, offset);
            var directory = Path.Combine(repoRoot, ".artifacts", "headless-unity");
            Directory.CreateDirectory(directory);
            var copy = Path.Combine(directory, Path.GetFileName(path));
            File.WriteAllBytes(copy, bytes);
            Console.WriteLine("HEADLESS: FontAsset layout normalized in a test-only Unity DLL copy; native UI rendering still needs an in-game test.");
            return copy;
        }
        return path;
    }
}
