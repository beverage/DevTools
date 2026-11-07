using DevTools.Testing;
using UnityEngine.Assertions;
using Verse;

namespace DevTools.Tests;

[TestFixture(TestType.Playing)]
[WarningsAllowed(Warning)]
internal class Test_SceneLoadLogs : IWorldGeneration, IMapGeneration
{
  private const string Warning = "Test_SceneLoadLogs: map generated";

  private bool logged;

  // A map-only regen fails after Test_SaveLoad: its load leaves the runner holding the old map.
  WorldGenerationSettings IWorldGeneration.WorldGenerationSettings => new();

  MapGenerationSettings IMapGeneration.MapGenerationSettings => new()
  {
    biome = MapGenerationSettings.Default.biome,
    mapGeneratorDef = MapGenerationSettings.Default.mapGeneratorDef,
    postGenerationActions = [.. MapGenerationSettings.Default.postGenerationActions, LogWarning]
  };

  private void LogWarning()
  {
    Log.Warning(Warning);
    logged = true;
  }

  [Test]
  private void NotPendingInFirstTest()
  {
    Assert.IsTrue(logged);
    using LogWatcher allowsNothing = new(TestRunner.Current.testManager.Config);
  }
}
