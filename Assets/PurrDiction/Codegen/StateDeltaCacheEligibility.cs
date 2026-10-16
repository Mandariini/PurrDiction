using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Purrdiction.Codegen
{
    public static class StateDeltaCacheEligibility
    {
        public static bool IsPlainValue(TypeReference type)
            => IsPlainValue(type, new HashSet<string>());

        private static bool IsPlainValue(TypeReference type, HashSet<string> visiting)
        {
            if (type == null || type.IsPointer || type.IsByReference || type.IsArray || type.IsGenericParameter)
                return false;
            switch (type.MetadataType)
            {
                case MetadataType.Boolean: case MetadataType.Char:
                case MetadataType.SByte: case MetadataType.Byte:
                case MetadataType.Int16: case MetadataType.UInt16:
                case MetadataType.Int32: case MetadataType.UInt32:
                case MetadataType.Int64: case MetadataType.UInt64:
                case MetadataType.Single: case MetadataType.Double:
                    return true;
                case MetadataType.IntPtr: case MetadataType.UIntPtr: case MetadataType.FunctionPointer:
                    return false;
            }
            if (!type.IsValueType || !visiting.Add(type.FullName))
                return false;
            try
            {
                var definition = type.Resolve();
                if (definition == null)
                    return false;
                if (definition.IsEnum)
                    return true;
                foreach (var field in definition.Fields)
                {
                    if (!field.IsStatic && !IsPlainValue(Bind(field.FieldType, type as GenericInstanceType), visiting))
                        return false;
                }
                return true;
            }
            catch (AssemblyResolutionException)
            {
                return false;
            }
            finally { visiting.Remove(type.FullName); }
        }

        private static TypeReference Bind(TypeReference type, GenericInstanceType context)
        {
            if (context == null || type == null)
                return type;
            if (type is GenericParameter parameter && parameter.Type == GenericParameterType.Type &&
                parameter.Position < context.GenericArguments.Count)
                return context.GenericArguments[parameter.Position];
            if (type is GenericInstanceType generic)
            {
                var bound = new GenericInstanceType(generic.ElementType);
                foreach (var argument in generic.GenericArguments)
                    bound.GenericArguments.Add(Bind(argument, context));
                return bound;
            }
            return type;
        }

        public static TypeReference FindStateType(TypeDefinition identity, string stateBaseName)
        {
            TypeReference current = identity.BaseType;
            var visiting = new HashSet<string>();
            while (current != null && visiting.Add(current.FullName))
            {
                if (current is GenericInstanceType generic && generic.ElementType.FullName == stateBaseName)
                    return generic.GenericArguments[0];
                var definition = current.Resolve();
                if (definition == null)
                    return null;
                current = Bind(definition.BaseType, current as GenericInstanceType);
            }
            return null;
        }

        public static void Enable(TypeDefinition identity, string methodName, string stateBaseName)
        {
            var current = identity;
            while (current != null && current.FullName != stateBaseName)
            {
                foreach (var method in current.Methods)
                    if (method.Name == methodName && !method.IsStatic && method.Parameters.Count == 0)
                        return;
                current = current.BaseType?.Resolve();
            }
            var enabled = new MethodDefinition(methodName,
                MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                identity.Module.TypeSystem.Boolean);
            var il = enabled.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Ret));
            identity.Methods.Add(enabled);
        }
    }
}
