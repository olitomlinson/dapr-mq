using DaprMQ.PerfReport;

try
{
    return Cli.Run(args, Console.Out);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
