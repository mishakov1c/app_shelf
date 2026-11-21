// Shared assembly metadata for .NET Framework and .NET 8 build paths.
// At release preparation, update the version AND the matching release notes below.
// The About dialog reads only metadata embedded in the compiled executable.
using System.Reflection;
[assembly: AssemblyVersion("2.19.0.0")]
[assembly: AssemblyFileVersion("2.19.0.0")]
[assembly: AssemblyInformationalVersion("2.19.0")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotesVersion", "2.19.0")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotes",
    "Фон главного окна теперь продолжается под нижней панелью.\n" +
    "Непрозрачность нижней панели настраивается в разделе «Внешний вид» (0–100%).\n" +
    "Строка состояния остаётся читаемой на светлых фонах благодаря отдельной подложке.\n" +
    "Сообщения автоматически сменяются подсказками через 5 секунд.\n" +
    "Кнопка «Подробнее» в компактном режиме выравнивается по высоте карточек.")]
