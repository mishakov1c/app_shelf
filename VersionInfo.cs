// Shared assembly metadata for .NET Framework and .NET 8 build paths.
// At release preparation, update the version AND the matching release notes below.
// The About dialog reads only metadata embedded in the compiled executable.
using System.Reflection;
[assembly: AssemblyVersion("2.24.1.0")]
[assembly: AssemblyFileVersion("2.24.1.0")]
[assembly: AssemblyInformationalVersion("2.24.1")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotesVersion", "2.24.1")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotes",
    "Адаптивная расстановка сохраняет группы, которые помещаются, и перемещает только выходящие за границы в ближайшее свободное место. При нехватке места используется вертикальная раскладка; исходные координаты сохраняются.")]
