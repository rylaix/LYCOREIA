// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
// compile support: some referenced game assemblies embed internal copies of these attributes without usable constructors
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = false, Inherited = false)]
    internal sealed class NullableAttribute : Attribute { public NullableAttribute(byte flag) { } public NullableAttribute(byte[] flags) { } }
    [AttributeUsage(AttributeTargets.All, AllowMultiple = false, Inherited = false)]
    internal sealed class NullableContextAttribute : Attribute { public NullableContextAttribute(byte flag) { } }
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    internal sealed class IsUnmanagedAttribute : Attribute { }
}
