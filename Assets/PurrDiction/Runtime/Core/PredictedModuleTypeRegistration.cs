using System;
using System.Reflection;
using PurrNet.Packing;
using PurrNet.Utils;

namespace PurrNet.Prediction
{
    internal static class PredictedModuleTypeRegistration
    {
        [RegisterPackers]
        private static void RegisterAllModuleTypes()
        {
            var moduleBase = typeof(PredictedModule);
#if UNITY_6000_4_OR_NEWER
            var assemblies = UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies();
            var length = assemblies.Count;
#else
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var length = assemblies.Length;
#endif
            var loaded = new Assembly[length];
            for (int a = 0; a < length; a++) loaded[a] = assemblies[a];
            var candidates = AssemblyDependencyScope.Find(loaded, moduleBase.Assembly);
            for (int a = 0; a < length; a++)
            {
                if (!candidates.Contains(assemblies[a])) continue;
                Type[] types;
                try
                {
                    types = DerivedTypeDiscovery.ReadIndex(assemblies[a],
                                "PurrNet.Prediction.Generated.ModuleTypeIndexV1") ?? assemblies[a].GetTypes();
                }
                catch { continue; }

                for (int t = 0; t < types.Length; t++)
                {
                    var type = types[t];
                    if (type.IsAbstract) continue;
                    if (!moduleBase.IsAssignableFrom(type)) continue;
                    Hasher.PrepareType(type);
                }
            }
        }
    }
}
