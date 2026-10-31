using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using JetBrains.Annotations;
using RimWorld;
using UnityEngine;
using Verse;

namespace DevTools.Testing;

/// <summary>
/// Test Manager for running unit tests in RimWorld. Tests can be run in isolation or be executed
/// as part of a test suite. This manager will handle switching between scenes and consolidating
/// test results in an explorer widget, allowing you to view each test class and its results.
/// <para/>
/// Due to Unity being single threaded, tests are run synchronously. This will block the
/// main thread and cause the application to hang for the duration of test execution. Because
/// RimWorld is so tightly coupled it's impossible to predict where it might call to Unity's API.
/// </summary>
[PublicAPI]
public class TestFixtureManager : IDevToolWithMenu, ITestManager
{
  private const string ManagerName = "Test Explorer";

  // TODO - expose toggle to config, cli, and/or test explorer
  internal static readonly bool BreakOnTestFailure = false;

  private static readonly string DevToolsName = typeof(Test).Assembly.GetName().Name;

  private static readonly FieldInfo DefOfBindingNow =
    typeof(DefOfHelper).GetField("bindingNow", BindingFlags.NonPublic | BindingFlags.Static);
  private static readonly MethodInfo DefOfBindDefsFor =
    typeof(DefOfHelper).GetMethod("BindDefsFor", BindingFlags.NonPublic | BindingFlags.Static, [typeof(Type)]);

  private ModContentPack mod;
  private readonly Dictionary<Assembly, ITestModule> modules = [];
  private readonly Dictionary<string, TestFixture> testFixtures = [];
  private int reloads;

  private Dialog_TestExplorer testExplorer;

  string ITestManager.ConfigName => "UnitTestConfig";

  public TestConfig Config { get; private set; }

  ITestConfig ITestManager.Config => Config;

  public IEnumerable<ITestFixture> TestFixtures => testFixtures.Values;

  string IDevToolWithMenu.Name => ManagerName;

  private ITestModule GetModule(Type type)
  {
    if (!modules.TryGetValue(type.Assembly, out ITestModule module))
    {
      module = new AssemblyModule(type.Assembly);
      modules.Add(type.Assembly, module);
    }
    return module;
  }

  public void OpenMenu()
  {
    testExplorer ??= new Dialog_TestExplorer(this);
    Find.WindowStack?.Add(testExplorer);
  }

  bool IDevTool.TryRegisterType(Type type)
  {
    TestFixtureAttribute attr = type.TryGetAttribute<TestFixtureAttribute>();
    if (attr is null || type.IsAbstract)
      return false;

    if (type.MissingRequiredMods())
      return false;

    ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
    if (constructors.Length != 1)
    {
      Log.Error($"Unable to register {type.Name}. It must have 1 public constructor.");
      return false;
    }

    foreach (TestFixture fixture in TestExtensions.CreateAllFixtures(CreateFixture, type, constructors[0]))
    {
      string key = $"{type.Namespace}.{fixture.Name}";
      if (fixture.Args.Length > 0)
      {
        key += $"({string.Join(',', fixture.Args)})";
      }
      testFixtures.TryAdd(key, fixture);
      fixture.AddFromType(type);
    }
    return true;

    TestFixture CreateFixture()
    {
      return new TestFixture(GetModule(type), type, attr.Type);
    }
  }

  bool IDevTool.Init(ModContentPack modContentPack)
  {
    mod = modContentPack;
    foreach (TestFixture testGroup in testFixtures.Values)
    {
      testGroup.SortByExecutionPriority();
    }
    Config = this.LoadConfig<TestConfig>(mod);
    if (Config == null)
      return false;

    Test.Discover(this);
    return true;
  }

  internal bool TryGetTestFixture(string fullName, out TestFixture testGroup)
  {
    return testFixtures.TryGetValue(fullName, out testGroup);
  }

  void ITestManager.OnTestRunnerStart()
  {
  }

  void ITestManager.OnTestRunnerEnd()
  {
    if (Config.log is { report: not null })
    {
      GenGeneric.InvokeStaticGenericMethod(typeof(Test), Config.log.report, nameof(Test.GenerateReport),
        args: [Config.log.folder, this]);
    }
    if (DevHarmony.Args is { exitOnFinish: true })
    {
      bool anyFailed = TestFixtures.Any(group => group.Status == Status.Failed);
      DevLog.Write($"Test runner finished. Result: {(anyFailed ? "Failed" : "Passed")}");
      Application.Quit(anyFailed ? 1 : 0);
      return;
    }
    OpenMenu();
  }

  public void RunAll()
  {
    TestRunner runner = new(this);
    runner.AddTestActions(Config);
    runner.Run();
  }

  public void ReloadTests()
  {
    if (TestRunner.Active)
    {
      Messages.Message("Unable to reload tests while tests are running.", MessageTypeDefOf.RejectInput);
      return;
    }

    TestAssemblyLoadContext context = null;
    List<Assembly> assemblies = [];
    try
    {
      List<Assembly> testAssemblies = mod.assemblies.loadedAssemblies.Where(IsTestAssembly)
       .Distinct()
       .ToList();
      HashSet<Assembly> toReload = TestAssembliesToReload(testAssemblies);
      if (toReload.Count > 0)
      {
        context = new TestAssemblyLoadContext($"{mod.Name} tests (reload {reloads + 1})");
        foreach (Assembly loaded in testAssemblies)
        {
          assemblies.Add(toReload.Contains(loaded) ? context.LoadFile(loaded.Location) : loaded);
        }
        foreach (Assembly copy in context.Copies)
        {
          copy.GetTypes();
        }
        BindDefOfs(context.Copies);
      }
    }
    catch (Exception ex)
    {
      Log.Error($"Exception thrown reloading tests for {mod.Name}. The loaded tests are kept.\n{ex}");
      return;
    }
    if (context == null)
    {
      WarnIfChangedOnDisk([]);
      Log.Message($"No test assemblies to reload for {mod.Name}.");
      return;
    }
    reloads++;

    modules.Clear();
    testFixtures.Clear();
    Test.Forget(this);
    IDevTool devTool = this;
    foreach (Type type in assemblies.SelectMany(assembly => assembly.GetTypes()))
    {
      try
      {
        devTool.TryRegisterType(type);
      }
      catch (Exception ex)
      {
        Log.Error($"Exception thrown loading type {type.Name} for {this}.\n{ex}");
      }
    }
    devTool.Init(mod);

    testExplorer?.Rebuild();
    WarnIfChangedOnDisk(context.Copies.ToList());
    int testCount = testFixtures.Values.Sum(fixture => fixture.TestFunctions.Count());
    Log.Message($"Reloaded {testCount} tests in {testFixtures.Count} fixtures for {mod.Name}.");
  }

  private static bool CanBindDefOfs => DefOfBindingNow != null && DefOfBindDefsFor != null;

  private static bool IsTestAssembly(Assembly assembly)
  {
    return assembly.GetReferencedAssemblies().Any(reference => reference.Name == DevToolsName);
  }

  private HashSet<Assembly> TestAssembliesToReload(List<Assembly> testAssemblies)
  {
    List<Assembly> loaded = mod.assemblies.loadedAssemblies;
    HashSet<Assembly> reload = [];
    foreach (Assembly assembly in testAssemblies)
    {
      string reason = ReasonToKeep(assembly);
      if (reason == null)
      {
        reload.Add(assembly);
      }
      else
      {
        Log.Warning($"Unable to reload {assembly.GetName().Name}. {reason}");
      }
    }
    bool keptMore = true;
    while (keptMore)
    {
      keptMore = false;
      foreach (Assembly kept in loaded.Where(assembly => !reload.Contains(assembly)).ToList())
      {
        foreach (AssemblyName reference in kept.GetReferencedAssemblies())
        {
          Assembly used = reload.FirstOrDefault(assembly => assembly.GetName().Name == reference.Name);
          if (used == null)
            continue;

          reload.Remove(used);
          keptMore = true;
          Log.Warning($"Unable to reload {used.GetName().Name}. {kept.GetName().Name} references it.");
        }
      }
    }
    return reload;
  }

  private static string ReasonToKeep(Assembly assembly)
  {
    if (assembly.Location.NullOrEmpty())
      return "It was not loaded from a file.";
    if (ExtendsGameType(assembly))
      return "It has a type that inherits from a game type.";
    if (!CanBindDefOfs && assembly.GetTypes().Any(type => type.HasAttribute<DefOf>()))
      return "It has a DefOf class, which this version of the game can't bind after startup.";
    return null;
  }

  private void WarnIfChangedOnDisk(List<Assembly> copies)
  {
    HashSet<string> reread = copies.Select(copy => copy.GetName().Name).ToHashSet();
    Dictionary<string, Assembly> modAssemblies = LoadedModManager.RunningModsListForReading
     .SelectMany(running => running.assemblies.loadedAssemblies)
     .DistinctBy(assembly => assembly.GetName().Name)
     .ToDictionary(assembly => assembly.GetName().Name);
    HashSet<Assembly> asLoaded = mod.assemblies.loadedAssemblies
     .Where(assembly => !reread.Contains(assembly.GetName().Name))
     .ToHashSet();
    Queue<Assembly> pending = new(copies.Concat(asLoaded));
    while (pending.TryDequeue(out Assembly assembly))
    {
      foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
      {
        if (!reread.Contains(reference.Name) &&
            modAssemblies.TryGetValue(reference.Name, out Assembly used) && asLoaded.Add(used))
        {
          pending.Enqueue(used);
        }
      }
    }
    foreach (Assembly assembly in asLoaded.Where(ChangedOnDisk))
    {
      Log.Warning($"{assembly.GetName().Name} has changed on disk. Restart the game to test the new build.");
    }
  }

  private static bool ChangedOnDisk(Assembly assembly)
  {
    if (assembly.Location.NullOrEmpty())
      return false;

    try
    {
      using FileStream stream = File.OpenRead(assembly.Location);
      using PEReader reader = new(stream);
      MetadataReader metadata = reader.GetMetadataReader();
      return metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != assembly.ManifestModule.ModuleVersionId;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException
                                 or InvalidOperationException)
    {
      return true;
    }
  }

  private static void BindDefOfs(IEnumerable<Assembly> copies)
  {
    List<Type> defOfs = copies.SelectMany(copy => copy.GetTypes())
     .Where(type => type.HasAttribute<DefOf>())
     .ToList();
    if (defOfs.Count == 0)
      return;

    if (!CanBindDefOfs)
    {
      Log.Warning($"Unable to bind {string.Join(", ", defOfs.Select(type => type.Name))}. This version of the " +
                  "game can't bind DefOf classes after startup, so they stay null until it restarts.");
      return;
    }
    object wasBinding = DefOfBindingNow.GetValue(null);
    DefOfBindingNow.SetValue(null, true);
    try
    {
      foreach (Type defOf in defOfs)
      {
        DefOfBindDefsFor.Invoke(null, [defOf]);
      }
    }
    finally
    {
      DefOfBindingNow.SetValue(null, wasBinding);
    }
  }

  private static bool ExtendsGameType(Assembly assembly)
  {
    Assembly game = typeof(Thing).Assembly;
    foreach (Type type in assembly.GetTypes())
    {
      for (Type baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
      {
        if (baseType.Assembly == game || baseType == typeof(UnityEngine.Object))
          return true;
      }
    }
    return false;
  }

  private static void TestExceptionHandler(Exception ex)
  {
    DelayedErrorWindowRequest.Add($"Exception thrown while running tests.\n{ex}",
      "TestFixtureManager Aborted Operation");
    Scribe.ForceStop();
    GenScene.GoToMainMenu();
  }
}