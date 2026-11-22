// Shared assembly metadata for .NET Framework and .NET 8 build paths.
// At release preparation, update the version AND the matching release notes below.
// The About dialog reads only metadata embedded in the compiled executable.
using System.Reflection;
[assembly: AssemblyVersion("2.20.0.0")]
[assembly: AssemblyFileVersion("2.20.0.0")]
[assembly: AssemblyInformationalVersion("2.20.0")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotesVersion", "2.20.0")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotes",
    "Настройки: шесть основных разделов; «Нижняя панель» перенесена во «Внешний вид».\n" +
    "Во «Внешнем виде» появились подразделы «Плитки и группы», «Фон главного окна» и «Нижняя панель».\n" +
    "Напоминания: у каждого одновременного срабатывания свои «Готово» и кнопки отложения.\n" +
    "Закрытие окна не подтверждает оставшиеся напоминания — они сохраняются до следующего запуска.\n" +
    "Существующие настройки, быстрые переключатели и расписания повторов сохранены.")]
