namespace NugetInspector;

public static class Config
{
    #pragma warning disable CA2211
    public static bool TRACE = false;
    public const bool TRACE_ARGS = false;
    public static bool TRACE_NET = false;
    public static bool TRACE_DEEP = false;
    public static bool TRACE_META = false;
    public static bool TRACE_OUTPUT = false;
    public const string NUGET_INSPECTOR_VERSION = "0.9.12";
    #pragma warning restore CA2211
}