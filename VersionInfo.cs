// Shared assembly metadata for .NET Framework and .NET 8 build paths.
// At release preparation, update the version AND the matching release notes below.
// The About dialog reads only metadata embedded in the compiled executable.
using System.Reflection;
[assembly: AssemblyVersion("2.24.0.0")]
[assembly: AssemblyFileVersion("2.24.0.0")]
[assembly: AssemblyInformationalVersion("2.24.0")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotesVersion", "2.24.0")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotes",
    "Подключаемые плагины API v1: менеджер, виджеты, индикаторы, отдельные процессы, явное доверие и независимые обновления пакетов.\n" +
    "Свободное полотно для групп приложений и виджетов с сохранением геометрии, миграцией прежних раскладок и четырьмя стратегиями адаптации.\n" +
    "Сеть и Happ выделены в самостоятельные плагины; RDP остаётся в ядре. NetworkPlugin устанавливается отдельно.\n" +
    "Выборочный состав ZIP-копий, резервирование приложения и разрешённых данных плагинов, исправления оформления и нижней панели.")]
