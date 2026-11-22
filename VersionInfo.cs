// Shared assembly metadata for .NET Framework and .NET 8 build paths.
// At release preparation, update the version AND the matching release notes below.
// The About dialog reads only metadata embedded in the compiled executable.
using System.Reflection;
[assembly: AssemblyVersion("2.21.0.0")]
[assembly: AssemblyFileVersion("2.21.0.0")]
[assembly: AssemblyInformationalVersion("2.21.0")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotesVersion", "2.21.0")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotes",
    "Группы: три варианта размещения — список, автоматическая сетка и девять свободных зон.\n" +
    "В списке доступна ширина по содержимому с выравниванием слева, по центру, справа или чередованием.\n" +
    "В сетке группы автоматически размещаются рядом и переносятся по доступной ширине.\n" +
    "В свободном режиме группу можно перетащить в зону; зона по умолчанию выбирается сеткой 3×3.\n" +
    "Переключить режим можно через «Быстрые настройки»; выбранный вариант сохраняется сразу.\n" +
    "Старые настройки работают без миграции; параметры других режимов при переключении сохраняются.")]
