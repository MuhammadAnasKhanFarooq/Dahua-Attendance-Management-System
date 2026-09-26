using System;

namespace DahuaAttendanceAPI
{
    // Global service provider container used only for rare legacy resolution scenarios.
    // Prefer constructor injection; this exists to support older resolution patterns in the codebase.
    internal static class AppServices
    {
        public static IServiceProvider? Instance;
    }
}
