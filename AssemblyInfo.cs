using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: AssemblyVersion("4.9.0.0")]
[assembly: AssemblyFileVersion("4.9.0.0")]

// GKME now compiles these sources into its own AOT image, so this attribute
// is no longer required for it. It is kept for any host that still embeds
// GKMD as a separate managed assembly.
[assembly: InternalsVisibleTo("GKME")]
