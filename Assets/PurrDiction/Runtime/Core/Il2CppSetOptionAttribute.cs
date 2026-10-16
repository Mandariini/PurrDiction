namespace Unity.IL2CPP.CompilerServices
{
    // IL2CPP recognises this attribute by its full name; Unity does not ship it as an API.
    internal enum Option
    {
        NullChecks = 1,
        ArrayBoundsChecks = 2,
        DivideByZeroChecks = 3
    }

    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct |
                           System.AttributeTargets.Method | System.AttributeTargets.Property,
        Inherited = false, AllowMultiple = true)]
    internal sealed class Il2CppSetOptionAttribute : System.Attribute
    {
        public Option Option { get; }
        public object Value { get; }

        public Il2CppSetOptionAttribute(Option option, object value)
        {
            Option = option;
            Value = value;
        }
    }
}
