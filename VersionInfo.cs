// Shared assembly metadata for .NET Framework and .NET 8 build paths.
// At release preparation, update the version AND the matching release notes below.
// The About dialog reads only metadata embedded in the compiled executable.
using System.Reflection;
[assembly: AssemblyVersion("2.23.0.0")]
[assembly: AssemblyFileVersion("2.23.0.0")]
[assembly: AssemblyInformationalVersion("2.23.0-beta.7")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotesVersion", "2.23.0-beta.7")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotes",
    "Автоматические резервные копии в выбранную папку с ограничением количества и объёма.\n" +
    "Перед обновлением сохраняются данные и предыдущий EXE.\n" +
    "В окне обновлений можно включить проверку бета-версий; стабильные обновления остаются доступны.\n" +
    "Окно резервных копий разделено на три сворачиваемые группы; статус копирования всегда виден.\n" +
    "Подборки вынесены в отдельный раздел настроек, резервные копии выделены отдельно; подсказка о закрытии окна перенесена в Поведение Windows.\n" +
    "Исправлена публикация резервных копий перед обновлением в папках облачной синхронизации.\n" +
    "Обновление переименованного EXE, повтор бэкапа при изменении данных, кэш проверки и обработка лимита GitHub.\n" +
    "Предварительный выпуск для ручной проверки.")]
