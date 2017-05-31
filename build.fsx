// include Fake libs

#r "./packages/build/FAKE/tools/FakeLib.dll"
#load "./.paket/load/net461/build/build.group.fsx"


open Fake
open Fake.EnvironmentHelper
open Fake.FileSystemHelper
open System
open System.IO
open System.Text
open System.Diagnostics
open System.Net.Sockets

open XPlot.GoogleCharts
open Newtonsoft.Json
open BenchmarkDotNet.Environments
open CsvHelper


let wrkDuration = 10

let benchmarkIterations = 1

#if MONO
let inferFrameworkPathOverride () =
    let mscorlib = "mscorlib.dll"
    let possibleFrameworkPaths =
        [ 
            "/Library/Frameworks/Mono.framework/Versions/Current/lib/mono/4.5/"
            "/usr/local/Cellar/mono/4.6.12/lib/mono/4.5/"
            "/usr/lib/mono/4.5/"
        ]

    possibleFrameworkPaths
    |> Seq.find (fun p ->IO.File.Exists(p @@ mscorlib))

Environment.SetEnvironmentVariable("FrameworkPathOverride", inferFrameworkPathOverride ())
#endif

module String =
    let contains (s2 : string) (s1 : string)  =
        s1.Contains s2

module Option =

    let getValueOt defaultValue optV =
        match optV with
        | Some s -> s
        | None -> defaultValue

// Directories
let srcDir = "./src"
let srcGlob = "src/**/**/*.fsproj"

let reportDir = "./reports"


let stringJoin (separator : string) (strings : string seq) =
    String.Join(separator, strings)

[<Measure>] type μs
[<Measure>] type s

let convertMicrotoS (μs : int<μs>) =
    μs / 1000000<μs/s>

[<Measure>] type req


//Proc helpers
let waitForExit ( proc : Process) = proc.WaitForExit()
let startProc fileName args (envVars : #seq<string*string>) workingDir=
    let psi = ProcessStartInfo(FileName = fileName, Arguments = args, WorkingDirectory = workingDir, UseShellExecute = false) 
    envVars |> Seq.iter(fun (k,v) -> 
        psi.EnvironmentVariables.[k] <- v)
    let proc = 
        psi
        |> Process.Start

    proc 

let startProc' fileName (args : #seq<string>) envVars workingDir =
    startProc fileName (String.Join(" ",args)) envVars workingDir

let execProcAndReturnMessages filename args =
    ExecProcessAndReturnMessages 
                    (fun psi ->
                        psi.FileName <- filename
                        psi.Arguments <-args
                    ) (TimeSpan.FromMinutes(1.))

let getProcessMessages (procResult : ProcessResult) =
    procResult.Messages

//System Helpers
let lsof args = execProcAndReturnMessages "lsof" args
 
let kill procId = 
    for i in 1 .. 10 do
        execProcAndReturnMessages "kill" (procId |> sprintf "-SIGINT %d") |> ignore
        Async.Sleep(100) |> Async.RunSynchronously

    execProcAndReturnMessages "kill" (procId |> sprintf "-%d") |> ignore

let mono args = execProcAndReturnMessages "mono" args

let dotnet args = execProcAndReturnMessages "dotnet" args



let getProcessIdByPort port =
    let result = lsof (sprintf "-ti :%d" port)
    result.Messages |> Seq.tryHead |> Option.map int
let getProcessesIdByPort port =
    let result = lsof (sprintf "-ti :%d" port)
    result.Messages |> Seq.map int

let waitForPortInUse  port =
    let mutable portInUse = false
    let sw = System.Diagnostics.Stopwatch.StartNew()
    while not portInUse do  
        if sw.ElapsedMilliseconds > 30000L then failwith "waited enough, must have failed"
        match getProcessIdByPort port with
        | Some _ -> portInUse <- true
        | _ -> ()
        Async.Sleep(1000) |> Async.RunSynchronously
        


let killProcessOnPort port =
    getProcessesIdByPort port |> Seq.iter kill 



// Targets
Target "Clean" (fun _ ->
    !! srcGlob
    |> Seq.collect(fun p -> 
        ["bin";"obj"] 
        |> Seq.map(fun sp ->
             IO.Path.GetDirectoryName p @@ sp)
        )
    |> CleanDirs
)


let dotnetRestore projFile =    
    DotNetCli.Restore (fun c ->
        { c with
            Project = projFile
        })

// --------------------------------------------------------------------------------------

type SystemInfo = {
    CPUModel : string
    ProcessorCount : int
    MonoVersion : string
    DotnetVersion : string
    OperatingSystem : string
}

let getSystemInfo () =
    let hostInfo =  HostEnvironmentInfo.GetCurrent()
    let benchInfo = BenchmarkEnvironmentInfo.GetCurrent()
    {
        CPUModel =  hostInfo.ProcessorName.Value
        ProcessorCount = hostInfo.ProcessorCount
        MonoVersion = benchInfo.RuntimeVersion
        DotnetVersion = hostInfo.DotNetCliVersion.Value
        OperatingSystem = hostInfo.OsVersion.Value
    }

let td inner = 
    sprintf "<td>%s</td>" inner
let tr tds=
    tds 
    |> stringJoin ""
    |> sprintf "<tr>%s</tr>"
let table trs =
    trs 
    |> stringJoin ""
    |> sprintf "<table>%s</table>"

let systemInfoToHtmlTable (sysInfo:SystemInfo) =
    let cpu = sysInfo.CPUModel |> td
    let os = sysInfo.OperatingSystem |> td
    let proc = sysInfo.ProcessorCount |> string |> td
    let monoV = sysInfo.MonoVersion|> td
    let dotnetV = sysInfo.DotnetVersion |> td

    table
        [
            tr [td "CPU"; cpu]
            tr [td "Operating System" ;os;]
            tr [td "Processor Count" ;proc;]
            tr [td "Mono Version" ;monoV;]
            tr [td "Dotnet Version" ;dotnetV;]
        ]

type ProjectInfo = {
    ProjectFile : string
    TargetFramework : string
    IsMono : bool
    WebServer : string
    WebFramework : string
}
    with member x.FriendlyName = sprintf "%s/%s on %s" x.WebServer x.WebFramework x.TargetFramework

type RoutesToTest = string


type BenchmarkParamters = {
    ProjectInfo : ProjectInfo
    RoutesToTest : RoutesToTest
}

//https://github.com/wg/wrk/blob/master/SCRIPTING
//{"bytes":217834904,"duration":30099407,"errors":{"connect":0,"read":182,"status":0,"timeout":0,"write":0},"requests":1785532}
type Error = {
    connect : int
    read : int
    status : int
    timeout : int
    write : int
}

type Summary = {
    bytes : int
    duration : int<μs> // in microseconds
    requests : int<req>
    errors : Error
}
    with 
        member this.RequestsPerSecond () =         
            this.requests/( convertMicrotoS this.duration )


type Latency = {
    min : float
    max : float
    mean : float
    stdev : float
}
type ProjectName = string

let writeToFile filePath str =
    System.IO.File.WriteAllText(filePath, str)
let getHtml (chart : GoogleChart) =
    chart.GetInlineHtml()

let wrk threads connections duration script url=
    let args = sprintf "-t%d -c%d -d%d -s %s %s" threads connections duration script url
    let result = ExecProcessAndReturnMessages 
                    (fun psi ->
                        psi.FileName <- "wrk"
                        psi.Arguments <-args
                    ) (TimeSpan.FromMinutes(5.))
    if result.OK
    then
        let revResults = 
            result.Messages
            |> Seq.rev
            |> Seq.cache
        (
            JsonConvert.DeserializeObject<Summary>(revResults |> Seq.head), 
            (JsonConvert.DeserializeObject<Latency>(revResults |> Seq.skip 1 |> Seq.head))
        )
    else result.Errors |> stringJoin "" |> failwith 
    
let port = 8083

let createPage systemInfo charts =
    sprintf
        """
        <!DOCTYPE html>
        <html>
            <head>
                <meta charset="UTF-8">
                <meta http-equiv="X-UA-Compatible" content="IE=edge" />
                <title>Google Chart</title>
                <script type="text/javascript" src="https://www.google.com/jsapi"></script>
                <script type="text/javascript">
                    google.load('visualization', '1', { 'packages': ['corechart'] });
                </script>
                <style>
                    table {
                        font-family: arial, sans-serif;
                        border-collapse: collapse;
                    }
                    td, th {
                        border: 1px solid #dddddd;
                        text-align: left;
                        padding: 8px;
                    }

                    tr:nth-child(even) {
                        background-color: #dddddd;
                    }
                </style>

            </head>
            <body>
                <h3> SystemInfo </h3>
                    %s
                <br>
                <h3> Results </h3>
                <div>%s</div>
            </body>
        </html>
    """ systemInfo charts



let IsRunningOnMono tf =
    match tf with
    | "net45" | "net451" | "net452" 
    | "net46" | "net461" | "net462" -> isMono
    | _ -> false

let selectRunner (projectInfo : ProjectInfo) =
    let startProc'' proc envVars args = startProc' proc args envVars

    let monoOptions () =
        [
            "--server"
            (if isMacOS then "--arch=64" else "") //osx by default runs on x86.  Other distros don't know about this flag so set it specific to osx.
        ]
        |> stringJoin " "
        |> sprintf "-mo=\"%s\""

    IO.Path.GetDirectoryName projectInfo.ProjectFile    
    |>  match projectInfo.TargetFramework with
        | "net45" | "net451" | "net452" 
        | "net46" | "net461" | "net462" -> 
            if isMono then
                [
                    "mono"
                    sprintf "-f %s" projectInfo.TargetFramework
                    monoOptions ()
                    "--restore"
                    "-c Release"
                    sprintf "-p %s" projectInfo.ProjectFile
                ]
                |> startProc'' "dotnet" [("MONO_THREADS_PER_CPU", "2000")]
            else 
                failwithf "lol who uses windows"
            
        | "netcoreapp1.0" | "netcoreapp1.1" -> 
            [
                "run"
                "-c Release"
                sprintf "-f %s" projectInfo.TargetFramework
                sprintf "-p %s" projectInfo.ProjectFile
            ]
            |> startProc'' "dotnet" []
        | _ -> failwithf "Unknown targetframework %s" projectInfo.TargetFramework

type Iteration = int
type BenchmarkResult = ProjectInfo * Summary * Latency * Iteration * RoutesToTest
let runBenchmark iteration (benchParam : BenchmarkParamters) =   
    try
        let projectInfo = benchParam.ProjectInfo
        let urlToTest = sprintf "http://127.0.0.1:8083%s" benchParam.RoutesToTest
        logfn "---------------> Starting %s <---------------" projectInfo.FriendlyName
        killProcessOnPort port 
        use proc = selectRunner projectInfo
        waitForPortInUse port
        let (summary, latency) = wrk 8 400 wrkDuration "./scripts/reportStatsViaJson.lua" urlToTest
        proc.Id |> kill
        //Have to kill process by port because dotnet run calls dotnet exec which has a different process id
        killProcessOnPort port 
        proc.Id |> kill
        logfn "---------------> Finished %s <---------------" projectInfo.FriendlyName
        Some (projectInfo, summary, latency, iteration, benchParam.RoutesToTest)
    with e -> 
        eprintfn "%A" e
        None

let mutable (results : seq<BenchmarkResult>) = null



let gatherProjectInfoAndRoutesToTest (projFile : string) =
    let doc = Xml.XmlDocument()
    doc.Load(projFile)
    let targetFrameworks = doc.GetElementsByTagName("TargetFrameworks").[0].InnerText.Split(';')
    let parts = 
        projFile.Split(IO.Path.DirectorySeparatorChar)
        |> Array.rev

    let webframework = parts.[1]
    let webserver = parts.[2]
    let projInfos =
        targetFrameworks
        |> Seq.map(fun tf -> 
        {
            ProjectFile = projFile
            TargetFramework = tf
            WebServer = webserver
            WebFramework = webframework
            IsMono = IsRunningOnMono tf
        })
    let routesToTest =
        DirectoryName projFile
        |> directoryInfo
        |> filesInDirMatching "rooutesToTest.txt"
        |> Seq.tryHead
        |> Option.map (string >> File.ReadAllLines)
        |> Option.getValueOt [|"/"|]

    routesToTest
    |> Seq.collect(
        fun s -> 
            projInfos
            |> Seq.map(fun p ->
                {ProjectInfo = p; RoutesToTest =s }
            )
    )
    
    
 

Target "DotnetRestore" (fun _ ->
        !! srcGlob
        |> Seq.toArray
        |> Array.iter dotnetRestore
 )



let (<||>) pred1 pred2 x =
        pred1 x || pred2 x

Target "Benchmark" (fun _ ->
    //Make sure nothing is on this port
    killProcessOnPort port
    let foo = 
        [1..benchmarkIterations]
        |> Seq.collect(fun i -> 
            !! srcGlob
            |> Seq.toList
            |> Seq.cache
            |> Seq.take 1
            // |> Seq.filter(String.contains "Freya" )
            // |> Seq.filter(String.contains "Giraffe" <||> String.contains "Kestrel/MVC" <||> String.contains "Kestrel/Plain"  )
            |> Seq.collect gatherProjectInfoAndRoutesToTest
            // |> Seq.filter(fun x -> x.TargetFramework |> String.contains "netcoreapp1")
            |> Seq.choose (runBenchmark i)
            |> Seq.toList
            |> Seq.cache
        )
        |> Seq.cache
    results <- foo
)




let dumpAllDataToConsole (results : seq<BenchmarkResult>)  =  
    results |> Seq.iter (printfn "%A")



type FlatResult = {
    WebServer : ProjectName
    WebFramework: string
    Route : string
    Iteration : int
    IsMono : bool
    TargetFramework : string
    TotalRequests : int<req>
    Duration: int<s>
    RequestsPerSecond : int<req/s>
    MaxLatency : float
    MinLatency : float
    MeanLatency : float
    StdDevLatency : float
}
    with
        static member OfBenchmarkResult ((proj, sum, lat, iteration, route) : BenchmarkResult) =
            {
                WebServer = proj.WebServer
                WebFramework = proj.WebFramework
                Route = route
                Iteration = iteration
                IsMono = proj.IsMono
                TargetFramework = proj.TargetFramework
                TotalRequests = sum.requests
                Duration = sum.duration |> convertMicrotoS
                RequestsPerSecond = sum.RequestsPerSecond()
                MaxLatency = lat.max
                MinLatency = lat.min
                MeanLatency = lat.mean
                StdDevLatency = lat.stdev
            }
        static member OfBenchmarkResults (results) = results |> Seq.map FlatResult.OfBenchmarkResult

let reportFileNameStart = (DateTimeOffset.UtcNow.ToString("o") |> sprintf "report-%s" )

let getReportPath extension = reportDir @@ (sprintf "%s.%s" reportFileNameStart extension )

let createTextReport (results : seq<BenchmarkResult>) =
    let mdTable =
        results 
        |> FlatResult.OfBenchmarkResults
        |> Seq.sortByDescending(fun tr -> tr.RequestsPerSecond)
        |> MarkdownLog.MarkDownBuilderExtensions.ToMarkdownTable
        |> string
    
    File.AppendAllText((getReportPath "md"), mdTable )
    mdTable
    |> printfn "%s"


let createCsvReport (results : seq<BenchmarkResult>) =
    let fileName = getReportPath "csv"
    let write () =
        use textWriter =  fileName |> File.CreateText
        use csv = new CsvWriter( textWriter )
        let resultsFlat = 
            results 
            |> FlatResult.OfBenchmarkResults
        csv.WriteRecords( resultsFlat )

    write ()
    fileName
    |> File.ReadAllLines
    |> Seq.iter(printfn "%A")


let createHtmlReport (results : seq<BenchmarkResult>) =  
    let ( _,firstResult,_,_,_) = results |> Seq.head 
    let duration = (firstResult.duration |> convertMicrotoS)
    let reportPath = getReportPath "html"
    let labels = results |> Seq.map(fun (proj,_,_, iteratoin,route) -> sprintf "%s-%s-%d" proj.FriendlyName route iteratoin)



    let totalRequestsChart =
        results
        |> Seq.map(fun (_,summary,latency,_,_) -> [("Total",summary.requests)])
        |> Chart.Bar
        |> Chart.WithLabels (labels)
        |> Chart.WithTitle (sprintf "Total Requests over %d seconds" duration)
    
    let requestsPerSecondChart =
        results
        |> Seq.map(fun (_,summary,_,_,_) -> [("Req/s", summary.RequestsPerSecond())])
        |> Chart.Bar
        |> Chart.WithLabels (labels)
        |> Chart.WithTitle (sprintf "Requests per second over %d seconds" duration)

        
    let meanLatencyChart =
        results
        |> Seq.map(fun (_,_,latency,_,_) -> 
            [
                // ("Min", latency.min/1000.); 
                // ("Max", latency.max/1000.); 
                ("Mean", latency.mean/1000.); 
            ])
        |> Chart.Bar
        |> Chart.WithLabels (labels)
        |> Chart.WithTitle (sprintf "Mean latency in milliseconds over %d seconds" duration)
    
    
    [totalRequestsChart;requestsPerSecondChart;meanLatencyChart]
    |> Seq.map getHtml
    |> stringJoin ""
    |> createPage (systemInfoToHtmlTable(getSystemInfo ()))
    |> writeToFile reportPath
    |> fun _ ->   
        try
            System.Diagnostics.Process.Start((Path.GetFullPath(reportPath)))  |> ignore
        with _ -> ()
    

let invoke f = f()


Target "GenerateReport" (fun _ ->
    [ 
        dumpAllDataToConsole
        createTextReport
        createCsvReport
        createHtmlReport
    ]
    |> Seq.map(fun f -> fun () -> f results)
    |> Seq.iter invoke
)


Target "SystemInfo" (fun _ ->
    getSystemInfo() |> printfn "%A"
)

// Build order
"Clean"
  ==> "DotnetRestore"
  ==> "Benchmark"
  ==> "GenerateReport"


// start build
RunTargetOrDefault "GenerateReport"
