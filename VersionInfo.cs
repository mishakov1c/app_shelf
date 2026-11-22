// Shared assembly metadata for .NET Framework and .NET 8 build paths.
// At release preparation, update the version AND the matching release notes below.
// The About dialog reads only metadata embedded in the compiled executable.
using System.Reflection;
[assembly: AssemblyVersion("2.19.3.0")]
[assembly: AssemblyFileVersion("2.19.3.0")]
[assembly: AssemblyInformationalVersion("2.19.3")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotesVersion", "2.19.3")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotes",
    "Обычные окна подтверждений, предупреждений и сообщений теперь оформлены в тёмном стиле AppShelf.\n" +
    "Сохранены существующие тексты и действия; критические системные ошибки запуска остались стандартными.\n" +
    "Механизм скачивания и установки обновлений не изменён.")]
