// Shared assembly metadata for .NET Framework and .NET 8 build paths.
// At release preparation, update the version AND the matching release notes below.
// The About dialog reads only metadata embedded in the compiled executable.
using System.Reflection;
[assembly: AssemblyVersion("2.21.1.0")]
[assembly: AssemblyFileVersion("2.21.1.0")]
[assembly: AssemblyInformationalVersion("2.21.1")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotesVersion", "2.21.1")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotes",
    "Исправлено свободное размещение групп на небольших экранах.\n" +
    "Верхние и нижние зоны занимают высоту содержимого, средняя — оставшееся место.\n" +
    "Если группы помещаются, лишняя вертикальная прокрутка не появляется.\n" +
    "Если высоты действительно недостаточно, прокрутка сохраняется и группы не обрезаются.\n" +
    "Перерасчёт работает при изменении размеров окна; плитки и настройки не меняются.")]
