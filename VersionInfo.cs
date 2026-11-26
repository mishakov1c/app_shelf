// Shared assembly metadata for .NET Framework and .NET 8 build paths.
// At release preparation, update the version AND the matching release notes below.
// The About dialog reads only metadata embedded in the compiled executable.
using System.Reflection;
[assembly: AssemblyVersion("2.24.0.0")]
[assembly: AssemblyFileVersion("2.24.0.0")]
[assembly: AssemblyInformationalVersion("2.24.0-beta.4")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotesVersion", "2.24.0-beta.4")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotes",
    "Свободное полотно — единственный режим размещения групп. Старые раскладки переносятся автоматически; сохранённая геометрия имеет приоритет. Оставлен подтверждаемый сброс расположения.\n" +
    "Исправлены высота свёрнутых групп, границы полотна при изменении окна и нежелательная перестройка при перетаскивании.\n" +
    "Исправлены тёмные выпадающие списки резервирования, дублирование нижней панели и действие по клику на индикатор с одной командой.\n" +
    "Менеджер плагинов позволяет добавлять индикаторы и виджеты и вручную проверять обновления через GitHub Releases для пакетов с объявленным источником.")]
