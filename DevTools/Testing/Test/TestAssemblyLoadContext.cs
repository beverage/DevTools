using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace DevTools.Testing;

internal sealed class TestAssemblyLoadContext(string name) : AssemblyLoadContext(name)
{
  private readonly Dictionary<string, Assembly> copies = [];

  private readonly Dictionary<string, Assembly> defaultAssemblies = Default.Assemblies
   .DistinctBy(assembly => assembly.GetName().Name)
   .ToDictionary(assembly => assembly.GetName().Name);

  public IEnumerable<Assembly> Copies => copies.Values;

  public Assembly LoadFile(string path)
  {
    using FileStream stream = File.OpenRead(path);
    Assembly assembly = LoadFromStream(stream);
    copies[assembly.GetName().Name] = assembly;
    return assembly;
  }

  protected override Assembly Load(AssemblyName assemblyName)
  {
    return copies.GetValueOrDefault(assemblyName.Name) ??
           defaultAssemblies.GetValueOrDefault(assemblyName.Name);
  }
}
