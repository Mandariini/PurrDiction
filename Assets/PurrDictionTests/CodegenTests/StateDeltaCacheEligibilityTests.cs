using System;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;
using NUnit.Framework;
using Purrdiction.Codegen;
using Application = UnityEngine.Application;

namespace PurrNet.Prediction.Tests.Codegen
{
    public sealed class StateDeltaCacheEligibilityTests
    {
        private static TypeDefinition Struct(ModuleDefinition module, string name)
        {
            var type = new TypeDefinition("Fixture", name, TypeAttributes.Public | TypeAttributes.Sealed,
                module.ImportReference(typeof(ValueType)));
            module.Types.Add(type);
            return type;
        }

        [Test]
        public void CompiledTransformAutomaticallyEnablesCachingWithoutComponentConfiguration()
        {
            string root = Directory.GetParent(Application.dataPath).FullName;
            using var module = ModuleDefinition.ReadModule(Path.Combine(root, "Library/ScriptAssemblies/PurrNet.Prediction.dll"));
            var transform = module.GetType("PurrNet.Prediction.PredictedTransform");
            Assert.That(transform, Is.Not.Null);
            var enabled = System.Linq.Enumerable.Single(transform.Methods, method => method.Name == "SupportsStateDeltaCaching");
            Assert.That(enabled.IsVirtual, Is.True);
            Assert.That(enabled.IsNewSlot, Is.False);
            Assert.That(enabled.Body.Instructions[0].OpCode, Is.EqualTo(OpCodes.Ldc_I4_1));
        }

        [Test]
        public void NestedValuesAndClosedGenericFieldsAreEligible()
        {
            using var module = ModuleDefinition.CreateModule("Fixture", ModuleKind.Dll);
            var inner = Struct(module, "Inner");
            inner.Fields.Add(new FieldDefinition("value", FieldAttributes.Private, module.TypeSystem.Single));
            inner.Fields.Add(new FieldDefinition("shared", FieldAttributes.Static, module.TypeSystem.Object));
            var wrapper = Struct(module, "Wrapper`1");
            wrapper.GenericParameters.Add(new GenericParameter("T", wrapper));
            wrapper.Fields.Add(new FieldDefinition("value", FieldAttributes.Private, wrapper.GenericParameters[0]));
            var closed = new GenericInstanceType(wrapper);
            closed.GenericArguments.Add(inner);
            Assert.That(StateDeltaCacheEligibility.IsPlainValue(closed), Is.True);
            closed.GenericArguments[0] = module.TypeSystem.String;
            Assert.That(StateDeltaCacheEligibility.IsPlainValue(closed), Is.False);
        }

        [Test]
        public void ReferencesArraysPointersAndNativeHandlesAreRejectedIncludingPrivateFields()
        {
            using var module = ModuleDefinition.CreateModule("Fixture", ModuleKind.Dll);
            foreach (var fieldType in new TypeReference[] { module.TypeSystem.Object, module.TypeSystem.String,
                         new ArrayType(module.TypeSystem.Int32), new PointerType(module.TypeSystem.Byte),
                         module.TypeSystem.IntPtr, module.TypeSystem.UIntPtr })
            {
                var state = Struct(module, "State" + module.Types.Count);
                state.Fields.Add(new FieldDefinition("hidden", FieldAttributes.Private, fieldType));
                Assert.That(StateDeltaCacheEligibility.IsPlainValue(state), Is.False, fieldType.FullName);
            }
        }

        [Test]
        public void StateArgumentsAreBoundThroughGenericIdentityInheritance()
        {
            using var module = ModuleDefinition.CreateModule("Fixture", ModuleKind.Dll);
            var stateBase = new TypeDefinition("Fixture", "Identity`1", TypeAttributes.Public, module.TypeSystem.Object);
            stateBase.GenericParameters.Add(new GenericParameter("T", stateBase));
            module.Types.Add(stateBase);
            var middle = new TypeDefinition("Fixture", "Middle`1", TypeAttributes.Public, null);
            middle.GenericParameters.Add(new GenericParameter("T", middle));
            var parent = new GenericInstanceType(stateBase);
            parent.GenericArguments.Add(middle.GenericParameters[0]);
            middle.BaseType = parent;
            module.Types.Add(middle);
            var closed = new GenericInstanceType(middle);
            closed.GenericArguments.Add(module.TypeSystem.Int64);
            var concrete = new TypeDefinition("Fixture", "Concrete", TypeAttributes.Public, closed);
            module.Types.Add(concrete);
            Assert.That(StateDeltaCacheEligibility.FindStateType(concrete, stateBase.FullName), Is.SameAs(module.TypeSystem.Int64));
        }

        [Test]
        public void GeneratedMethodReusesTheVirtualSlotAndPreservesExistingOverrides()
        {
            using var module = ModuleDefinition.CreateModule("Fixture", ModuleKind.Dll);
            var parent = new TypeDefinition("Fixture", "Identity", TypeAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(parent);
            var child = new TypeDefinition("Fixture", "Child", TypeAttributes.Public, parent);
            module.Types.Add(child);
            StateDeltaCacheEligibility.Enable(child, "SupportsStateDeltaCaching", parent.FullName);
            var method = child.Methods[0];
            Assert.That(method.IsVirtual, Is.True);
            Assert.That(method.IsNewSlot, Is.False);
            Assert.That(method.Body.Instructions[0].OpCode, Is.EqualTo(OpCodes.Ldc_I4_1));
            var grandchild = new TypeDefinition("Fixture", "Grandchild", TypeAttributes.Public, child);
            module.Types.Add(grandchild);
            StateDeltaCacheEligibility.Enable(grandchild, "SupportsStateDeltaCaching", parent.FullName);
            Assert.That(grandchild.Methods, Is.Empty, "an inherited decision must be respected");
        }
    }
}
