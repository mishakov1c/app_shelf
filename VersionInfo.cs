// Shared assembly metadata for .NET Framework and .NET 8 build paths.
// At release preparation, update the version AND the matching release notes below.
// The About dialog reads only metadata embedded in the compiled executable.
using System.Reflection;
[assembly: AssemblyVersion("2.24.0.0")]
[assembly: AssemblyFileVersion("2.24.0.0")]
[assembly: AssemblyInformationalVersion("2.24.0-beta.3")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotesVersion", "2.24.0-beta.3")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotes",
    "Сеть вынесена в независимый NetworkPlugin 1.0.0 Preview: проверки ICMP, HTTPS и TCP, диагностика и три индикатора.\n" +
    "При первом включении предложен перенос прежних настроек; существующие данные плагина сохраняются.\n" +
    "Happ вынесен в отдельный приватный плагин; RDP остаётся в ядре.\n" +
    "Исправлены перенос заголовков, изменение размеров и захват мыши на свободном полотне.")]
