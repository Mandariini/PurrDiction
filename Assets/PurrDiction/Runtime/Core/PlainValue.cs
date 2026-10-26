using System;
using System.Collections.Generic;
using System.Reflection;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Whether T holds only primitives, enums and structs of them: no references and no pointers.
    /// Such a value copies bitwise and owns nothing, so a shared row table may copy it freely and
    /// never dispose it. The same rule codegen uses to enable state delta caching.
    /// </summary>
    internal static class PlainValue<T>
    {
        internal static readonly bool isPlain = PlainValue.Check(typeof(T), new HashSet<Type>());
    }

    internal static class PlainValue
    {
        internal static bool Check(Type type, HashSet<Type> visiting)
        {
            if (type.IsPrimitive)
                return type != typeof(IntPtr) && type != typeof(UIntPtr);
            if (type.IsEnum)
                return true;
            if (!type.IsValueType || type.IsPointer || type.IsByRef || !visiting.Add(type))
                return false;
            try
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!Check(field.FieldType, visiting))
                        return false;
                }
                return true;
            }
            finally
            {
                visiting.Remove(type);
            }
        }
    }
}
