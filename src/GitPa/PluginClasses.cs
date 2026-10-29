using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace GitPa;

/// <summary>Finds the plug-in and custom workflow activity classes in an assembly by reading its metadata.</summary>
public static class PluginClasses
{
    const string PluginInterface = "Microsoft.Xrm.Sdk.IPlugin";
    const string WorkflowActivityBase = "System.Activities.CodeActivity";

    /// <summary>
    /// Full names (as Dataverse stores them in plugintype.typename) of the public, non-abstract classes that
    /// implement IPlugin or derive from CodeActivity. The assembly is read, never loaded or run.
    /// </summary>
    public static IReadOnlySet<string> Read(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();

        var classes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            var visibility = type.Attributes & TypeAttributes.VisibilityMask;
            if ((type.Attributes & (TypeAttributes.Interface | TypeAttributes.Abstract)) != 0
                || visibility is not (TypeAttributes.Public or TypeAttributes.NestedPublic))
                continue;
            if (IsPluginClass(metadata, handle))
                classes.Add(FullName(metadata, handle));
        }
        return classes;
    }

    static bool IsPluginClass(MetadataReader metadata, TypeDefinitionHandle handle, int depth = 0)
    {
        if (depth > 50)
            return false; // malformed inheritance chain
        var type = metadata.GetTypeDefinition(handle);
        if (Implements(metadata, type, depth))
            return true;
        if (type.BaseType.IsNil)
            return false;
        if (NameOf(metadata, type.BaseType) == WorkflowActivityBase)
            return true;
        return type.BaseType.Kind == HandleKind.TypeDefinition
            && IsPluginClass(metadata, (TypeDefinitionHandle)type.BaseType, depth + 1);
    }

    /// <summary>Whether the type implements IPlugin, directly or through an interface that extends it.</summary>
    static bool Implements(MetadataReader metadata, TypeDefinition type, int depth) =>
        type.GetInterfaceImplementations().Any(i =>
        {
            var iface = metadata.GetInterfaceImplementation(i).Interface;
            return NameOf(metadata, iface) == PluginInterface
                || (iface.Kind == HandleKind.TypeDefinition && depth < 50
                    && Implements(metadata, metadata.GetTypeDefinition((TypeDefinitionHandle)iface), depth + 1));
        });

    static string NameOf(MetadataReader metadata, EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => FullName(metadata, (TypeDefinitionHandle)handle),
        HandleKind.TypeReference => Join(metadata,
            metadata.GetTypeReference((TypeReferenceHandle)handle).Namespace,
            metadata.GetTypeReference((TypeReferenceHandle)handle).Name),
        _ => "", // generic instantiations and the like are never IPlugin or CodeActivity themselves
    };

    static string FullName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        var declaring = type.GetDeclaringType();
        return declaring.IsNil
            ? Join(metadata, type.Namespace, type.Name)
            : $"{FullName(metadata, declaring)}+{metadata.GetString(type.Name)}";
    }

    static string Join(MetadataReader metadata, StringHandle ns, StringHandle name) =>
        ns.IsNil || metadata.GetString(ns).Length == 0
            ? metadata.GetString(name)
            : $"{metadata.GetString(ns)}.{metadata.GetString(name)}";
}
