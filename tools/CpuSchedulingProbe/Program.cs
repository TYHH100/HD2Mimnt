using CpuSchedulingProbe;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This probe only supports Windows.");
    return 2;
}

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "topology" => Commands.Topology(),
        "spinner" => Spinner.Run(args[1..]),
        "sets" => Commands.Sets(args[1..]),
        "apply" => Commands.Apply(args[1..]),
        "restore" => Commands.Restore(args[1..]),
        "strict-test" => Commands.StrictTest(args[1..]),
        "denied" => Commands.Denied(),
        "experiment" => Experiment.Run(args[1..]),
        _ => Unknown(args[0])
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine("ERROR: " + ex.Message);
    Console.Error.WriteLine(ex);
    return 1;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    PrintUsage();
    return 2;
}

static void PrintUsage()
{
    Console.WriteLine("""
Usage: CpuSchedulingProbe <command> [options]

Commands:
  topology                       Dump CPU sets and P/E classification (read-only)
  spinner                        Disposable test target whose threads report their processor
    --seconds N                  Run time in seconds (default 20)
    --threads N                  Initial worker threads (default 4)
    --interval MS                Sleep between samples (default 20)
    --add-thread-after MS        Create one more worker after N ms
  sets --pid PID                 Show default CPU sets, affinity and thread-selected sets
  apply --pid PID --mode prefer  Apply process default CPU sets
    --sets ID,ID,...             Explicit CPU set IDs (required if topology is unsupported)
  apply --pid PID --mode strict  Apply process affinity mask
    --mask HEX                   Explicit mask (required if topology is unsupported)
  restore --pid PID [--force]    Restore the state saved by apply
  strict-test [--seconds N]      Spawn a disposable process and round-trip affinity
  denied                         Probe OpenProcess access on protected system processes
  experiment                     Automated before/after CPU Sets experiment
    --sets ID,ID,...             Force the allowed CPU sets
    --baseline-ms N              Baseline window (default 2500)
    --applied-ms N               Applied window (default 4000)
    --restored-ms N              Restored window (default 2500)
""");
}