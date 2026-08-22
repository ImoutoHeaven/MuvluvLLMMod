namespace System.Runtime.CompilerServices;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Event | AttributeTargets.Field | AttributeTargets.GenericParameter | AttributeTargets.Module | AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue)]
internal sealed class NullableAttribute : Attribute
{
    public NullableAttribute(byte value) { }
    public NullableAttribute(byte[] value) { }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Delegate | AttributeTargets.Interface | AttributeTargets.Method | AttributeTargets.Module | AttributeTargets.Struct)]
internal sealed class NullableContextAttribute : Attribute
{
    public NullableContextAttribute(byte value) { }
}
