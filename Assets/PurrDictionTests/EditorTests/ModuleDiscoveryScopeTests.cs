using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Utils;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class ModuleDiscoveryScopeTests
    {
        [Test]
        public void ScopedDiscoveryRetainsEveryModuleAndItsOriginalOrder()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var candidates = AssemblyDependencyScope.Find(assemblies, typeof(PredictedModule).Assembly);
            var expected = Discover(assemblies, null);
            var actual = Discover(assemblies, candidates);
            Assert.That(expected, Is.Not.Empty, "The fixture must contain real module classes");
            Assert.That(actual, Is.EqualTo(expected));
        }

        private static List<string> Discover(Assembly[] assemblies, HashSet<Assembly> candidates)
        {
            var result = new List<string>();
            foreach (var assembly in assemblies)
            {
                if (candidates != null && !candidates.Contains(assembly)) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch { continue; }
                foreach (var type in types)
                    if (!type.IsAbstract && typeof(PredictedModule).IsAssignableFrom(type))
                        result.Add(type.AssemblyQualifiedName);
            }
            return result;
        }
    }
}
