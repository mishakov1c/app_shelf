// Shared assembly metadata for .NET Framework and .NET 8 build paths.
// At release preparation, update the version AND the matching release notes below.
// The About dialog reads only metadata embedded in the compiled executable.
using System.Reflection;
[assembly: AssemblyVersion("2.24.4.0")]
[assembly: AssemblyFileVersion("2.24.4.0")]
[assembly: AssemblyInformationalVersion("2.24.4")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotesVersion", "2.24.4")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotes",
    "Резервное копирование добровольное: для новых и ранее ненастроенных профилей выключено, действующие настройки сохраняются.\n" +
    "Копии перед обновлением AppShelf и плагинов управляются отдельной настройкой. Ручной экспорт и восстановление доступны без общей папки резервирования. Технические механизмы отката сохранены.")]
