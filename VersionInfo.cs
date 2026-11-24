// Shared assembly metadata for .NET Framework and .NET 8 build paths.
// At release preparation, update the version AND the matching release notes below.
// The About dialog reads only metadata embedded in the compiled executable.
using System.Reflection;
[assembly: AssemblyVersion("2.23.0.0")]
[assembly: AssemblyFileVersion("2.23.0.0")]
[assembly: AssemblyInformationalVersion("2.23.0")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotesVersion", "2.23.0")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotes",
    "Автоматические резервные копии данных в выбранной папке, с настройками периодичности, количества и объёма.\n" +
    "Перед обновлением сохраняются данные и предыдущий EXE; при изменении данных копирование повторяется.\n" +
    "Перед восстановлением создаётся страховочная копия; после импорта приложение полностью завершается.\n" +
    "Необязательный канал бета-версий и обновление переименованного EXE.\n" +
    "Кэш проверки обновлений, обработка ограничений GitHub API и время последней успешной проверки.\n" +
    "Форматирование Markdown в окне обновлений, сворачиваемые группы резервного копирования и отдельный раздел настроек «Подборки».")]
