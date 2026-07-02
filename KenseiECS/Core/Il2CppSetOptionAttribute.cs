#if ENABLE_IL2CPP
using System;

// Unity does not ship this attribute — IL2CPP recognizes it by its full name,
// so every assembly that uses it must define its own internal copy.
namespace Unity.IL2CPP.CompilerServices {
    internal enum Option {
        NullChecks = 1,
        ArrayBoundsChecks = 2,
        DivideByZeroChecks = 3
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Property, Inherited = false, AllowMultiple = true)]
    internal sealed class Il2CppSetOptionAttribute : Attribute {
        public Option Option { get; }
        public object Value { get; }

        public Il2CppSetOptionAttribute(Option option, object value) {
            Option = option;
            Value = value;
        }
    }
}
#endif
