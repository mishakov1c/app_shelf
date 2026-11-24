// Shared assembly metadata for .NET Framework and .NET 8 build paths.
// At release preparation, update the version AND the matching release notes below.
// The About dialog reads only metadata embedded in the compiled executable.
using System.Reflection;
[assembly: AssemblyVersion("2.22.0.0")]
[assembly: AssemblyFileVersion("2.22.0.0")]
[assembly: AssemblyInformationalVersion("2.22.0")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotesVersion", "2.22.0")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotes",
    "Кнопка сворачивания теперь всегда оставляет окно на панели задач Windows.\n" +
    "Новая настройка «При закрытии окна сворачивать в трей» определяет действие крестика и Alt+F4.\n" +
    "Команда «Выход» в трее всегда полностью завершает AppShelf.\n" +
    "Сохранены автозапуск скрытым, Ctrl+Alt+Space и прежние параметры пользователя.\n" +
    "Погода, RDP и нижняя панель открывают сразу нужные разделы настроек.")]
