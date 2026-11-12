using System.Runtime.CompilerServices;

// The services keep their internal visibility: the two front ends and the tests are the only consumers.
[assembly: InternalsVisibleTo("DeadWare")]
[assembly: InternalsVisibleTo("DeadWare.WinUI")]
[assembly: InternalsVisibleTo("DeadWare.App.Tests")]
