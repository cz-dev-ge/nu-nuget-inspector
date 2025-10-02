namespace NugetInspector;

public static class Config
{
    #pragma warning disable CA2211
    public static bool TRACE = false;
    public static bool TRACE_ARGS = false;
    public static bool TRACE_NET = false;
    public static bool TRACE_DEEP = false;
    public static bool TRACE_META = false;
    public static bool TRACE_OUTPUT = false;
    public const string NugetInspectorVersion = "0.9.12";
    #pragma warning restore CA2211
}

public static class Log
{
    public static void Info(string message)
    {
        Console.WriteLine( message );
    }
    
    public static void Trace(string message)
    {
        if (Config.TRACE)
            Console.WriteLine( message );
    }
    
    public static void TraceMeta(string message)
    {
        if (Config.TRACE_META)
            Console.WriteLine( message );
    }
    
    public static void TraceDeep(string message)
    {
        if (Config.TRACE_DEEP)
            Console.WriteLine( message );
    }
    
    public static void TraceArgs(string message)
    {
        if (Config.TRACE_ARGS)
            Console.WriteLine( message );
    }
}