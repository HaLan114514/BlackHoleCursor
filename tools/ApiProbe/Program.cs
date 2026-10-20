using System.Reflection;

var assembly = Assembly.Load("Vortice.DirectComposition");
foreach (var t in assembly.GetExportedTypes().Take(40))
{
    Console.WriteLine(t.FullName);
}

Console.WriteLine("--- statics ---");
foreach (var t in assembly.GetExportedTypes())
{
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
    {
        Console.WriteLine(t.Name + " :: " + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
    }
}

Console.WriteLine("--- device/visual ---");
foreach (var name in new[] { "Vortice.DirectComposition.IDCompositionDevice", "Vortice.DirectComposition.IDCompositionVisual", "Vortice.DirectComposition.IDCompositionTarget" })
{
    var t = assembly.GetType(name);
    if (t is null) { Console.WriteLine(name + " NOT FOUND"); continue; }
    foreach (var m in t.GetMethods().Where(m => m.Name.Contains("Create") || m.Name.Contains("Commit") || m.Name.Contains("Content") || m.Name.Contains("Root")))
    {
        Console.WriteLine(t.Name + " :: " + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
    }
}
