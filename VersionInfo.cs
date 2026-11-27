// Shared assembly metadata for .NET Framework and .NET 8 build paths.
// At release preparation, update the version AND the matching release notes below.
// The About dialog reads only metadata embedded in the compiled executable.
using System.Reflection;
[assembly: AssemblyVersion("2.24.3.0")]
[assembly: AssemblyFileVersion("2.24.3.0")]
[assembly: AssemblyInformationalVersion("2.24.3")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotesVersion", "2.24.3")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotes",
    "Исправлены лишние полосы прокрутки свободного полотна: каждая полоса появляется только при переполнении. Координаты и индивидуальные раскладки сохраняются.\n" +
    "Индикаторы нижней панели в одной строке выровнены по высоте. Правый клик по индикатору открывает его действия; общее меню доступно на самой панели и по кнопке «⋯».")]
