// Shared assembly metadata for .NET Framework and .NET 8 build paths.
// At release preparation, update the version AND the matching release notes below.
// The About dialog reads only metadata embedded in the compiled executable.
using System.Reflection;
[assembly: AssemblyVersion("2.24.0.0")]
[assembly: AssemblyFileVersion("2.24.0.0")]
[assembly: AssemblyInformationalVersion("2.24.0-beta.1")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotesVersion", "2.24.0-beta.1")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotes",
    "Новая архитектура плагинов: локальные пакеты .asplugin и отдельные процессы.\n" +
    "Менеджер плагинов: установка с подтверждением доверия, включение, обновление и удаление.\n" +
    "Команды, собственные окна, настройки и уведомления плагинов.\n" +
    "Полные ZIP-бэкапы с EXE и пакетами плагинов; резервирование данных каждого плагина по отдельному согласию.\n" +
    "Безопасное извлечение программы и пакетов из ZIP без автоматического запуска; явное восстановление данных.\n" +
    "Демонстрационный плагин «Заметки» поставляется отдельным пакетом. Это предварительная версия API плагинов.")]
