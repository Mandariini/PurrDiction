using System.Linq;
using NUnit.Framework;
using PurrNet.Prediction;
using PurrNet.Utils;

public sealed class ModuleDiscoveryIndexTests
{
    [Test]
    public void CompiledRuntimeIndexMatchesLegacyModuleDiscoveryOrder()
    {
        var assembly = typeof(PredictedModule).Assembly;
        var indexed = DerivedTypeDiscovery.ReadIndex(assembly, "PurrNet.Prediction.Generated.ModuleTypeIndexV1");
        Assert.That(indexed, Is.Not.Null);
        var expected = assembly.GetTypes().Where(t => !t.IsAbstract && typeof(PredictedModule).IsAssignableFrom(t)).ToArray();
        Assert.That(expected.Length, Is.GreaterThan(0));
        CollectionAssert.AreEqual(expected, indexed.Where(t => !t.IsAbstract && typeof(PredictedModule).IsAssignableFrom(t)).ToArray());
    }
}
