using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Spectre.Console;
using System.Diagnostics;
using System.Security.Cryptography;

if (!(TraceEventSession.IsElevated() ?? false))
{
    Console.Out.WriteLine("To turn on ETW events you need to be Administrator, please run from an Admin process.");
    return;
}

var generatorTimingInfo = new Dictionary<int, ProcessInfo>();

// create a new trace session
using var session = new TraceEventSession("Microsoft-CodeAnalysis-Generators-Trace-Session");

// ensure we also dispose the session if the user Control + C's
Console.CancelKeyPress += (s, e) => session.Dispose();

// capture the generator driver run time
session.Source.Dynamic.AddCallbackForProviderEvent("Microsoft-CodeAnalysis-General", "GeneratorDriverRunTime/Stop", (TraceEvent data) =>
{
    // We store the overall execution time in the first slot of the info.
    EnsureProcessSlot(data.ProcessID);
    generatorTimingInfo[data.ProcessID].generators[0].executionTimes.Add((long)data.PayloadByName("elapsedTicks"));
});

// capture the individual generator run times
session.Source.Dynamic.AddCallbackForProviderEvent("Microsoft-CodeAnalysis-General", "SingleGeneratorRunTime/Stop", (TraceEvent data) =>
{
    EnsureProcessSlot(data.ProcessID);

    var generatorName = (string)data.PayloadByName("generatorName");
    var assemblyPath = (string)data.PayloadByName("assemblyPath");
    var ticks = (long)data.PayloadByName("elapsedTicks");

    var processInfo = generatorTimingInfo[data.ProcessID];

    var info = processInfo.generators.SingleOrDefault(i => i.name == generatorName && i.assembly == assemblyPath);
    if (info is null)
    {
        info = new GeneratorInfo(generatorName, assemblyPath, new List<long>() { });
        processInfo.generators.Add(info);
    }

    info.executionTimes.Add((long)data.PayloadByName("elapsedTicks"));
});

Console.WriteLine("Starting Collection. Waiting for data...");
_ = RenderData();
session.EnableProvider("Microsoft-CodeAnalysis-General");
session.Source.Process();

void EnsureProcessSlot(int processID)
{
    if (!generatorTimingInfo.ContainsKey(processID))
    {
        var processName = Process.GetProcessById(processID).ProcessName;

        generatorTimingInfo[processID] = new ProcessInfo(processName, new List<GeneratorInfo>() { new GeneratorInfo("GeneratorDriver", "", new List<long>() { }) });
    }
}

async Task RenderData()
{
    while (true)
    {
        await Task.Delay(1000);
        if (generatorTimingInfo.Count == 0)
            continue;

        AnsiConsole.Cursor.SetPosition(0, 0);
        WriteLine("Generators:");

        var table = new Table();
        table.AddColumn("Generator Name");
        table.AddColumn("Assembly Path");
        table.AddColumn("PIDs");


        var pidGrouped = generatorTimingInfo.SelectMany(e => e.Value.generators.Skip(1).Select(g => new { g.name, g.assembly, pid = e.Key })).GroupBy(g => g.assembly + g.name).Select(g => new { name = g.First().name, assembly = g.First().assembly, pids = g.Select(g2 => g2.pid) });
        foreach (var group in pidGrouped)
        {
            table.AddRow(group.name, group.assembly, string.Join(",", group.pids));
        }
        AnsiConsole.Render(table);

        WriteLine("");
        foreach (var pid in generatorTimingInfo)
        {
            WriteLine($"{pid.Value.name} (PID {pid.Key}):");

            table = new Table();
            table.AddColumn("Generator");
            table.AddColumn("Last Run Time (ms)");
            table.AddColumn("Average Time (ms)");
            table.AddColumn("Cumulative Time");
            table.AddColumn("Count");

            RenderRow(pid.Value.generators[0]);
            table.AddEmptyRow();
            foreach (var entry in pid.Value.generators.Skip(1))
            {
                RenderRow(entry);
            }
            AnsiConsole.Render(table);

            void RenderRow(GeneratorInfo info)
            {
                if (info.executionTimes.Count == 0)
                {
                    table.AddEmptyRow();
                    return;
                }

                var lastRun = TimeSpan.FromTicks(info.executionTimes.Last()).TotalMilliseconds.ToString();
                var average = TimeSpan.FromTicks((long)info.executionTimes.Average()).TotalMilliseconds.ToString();
                var cumulative = TimeSpan.FromTicks(info.executionTimes.Sum()).ToString();

                table.AddRow(info.name, lastRun, average, cumulative, info.executionTimes.Count.ToString());
            }
        }
    }

    void WriteLine(string s) => Console.WriteLine(s.PadRight(Console.BufferWidth));
}

record GeneratorInfo(string name, string assembly, List<long> executionTimes);
record ProcessInfo(string name, List<GeneratorInfo> generators);