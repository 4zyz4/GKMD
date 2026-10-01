using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: AssemblyVersion("4.3.0.0")]
[assembly: AssemblyFileVersion("4.3.0.0")]

// The GKME application links the engine as a separate assembly but still
// reaches into a few internals (the USB/IP transport installer in
// particular). Keep that door open without widening the public API.
[assembly: InternalsVisibleTo("GKME")]
