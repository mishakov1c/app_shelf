// Shared assembly metadata for .NET Framework and .NET 8 build paths.
// At release preparation, update the version AND the matching release notes below.
// The About dialog reads only metadata embedded in the compiled executable.
using System.Reflection;
[assembly: AssemblyVersion("2.24.3.0")]
[assembly: AssemblyFileVersion("2.24.3.0")]
[assembly: AssemblyInformationalVersion("2.24.3")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotesVersion", "2.24.3")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotes",
    "Исправлены лишние полосы прокрутки свободного полотна: если группы помещаются в видимой области, прокрутка не появляется. При реальном переполнении прокрутка сохраняется; координаты и индивидуальные раскладки не изменяются.")]
