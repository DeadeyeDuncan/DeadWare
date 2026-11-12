using System.Runtime.CompilerServices;

// The services keep their internal visibility: the app and its tests are the only consumers.
[assembly: InternalsVisibleTo("DeadWare")]
[assembly: InternalsVisibleTo("DeadWare.App.Tests")]
