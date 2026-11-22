// Shared assembly metadata for .NET Framework and .NET 8 build paths.
// At release preparation, update the version AND the matching release notes below.
// The About dialog reads only metadata embedded in the compiled executable.
using System.Reflection;
[assembly: AssemblyVersion("2.19.1.0")]
[assembly: AssemblyFileVersion("2.19.1.0")]
[assembly: AssemblyInformationalVersion("2.19.1")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotesVersion", "2.19.1")]
[assembly: AssemblyMetadata("AppShelf.ReleaseNotes",
    "Добавлена ручная проверка новых версий через GitHub Releases.\n" +
    "Обновление скачивается с проверкой размера, SHA-256 и версии EXE.\n" +
    "Предыдущий EXE сохраняется для отката при установке.\n" +
    "Добавлены сведения о GPL-3.0-only и атрибуция погодных источников.\n" +
    "Улучшена поддержка MET Norway: сжатие, HTTPS-перенаправления и HTTP-кеш.")]
