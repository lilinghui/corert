﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ReadyToRun.SuperIlc
{
    public class BuildFolderSet
    {
        private IEnumerable<BuildFolder> _buildFolders;

        private IEnumerable<CompilerRunner> _compilerRunners;

        private BuildOptions _options;

        private Buckets _frameworkCompilationFailureBuckets;

        private Buckets _compilationFailureBuckets;

        private Buckets _executionFailureBuckets;

        private long _frameworkCompilationMilliseconds;

        private long _compilationMilliseconds;

        private long _executionMilliseconds;

        private long _buildMilliseconds;

        public BuildFolderSet(
            IEnumerable<BuildFolder> buildFolders,
            IEnumerable<CompilerRunner> compilerRunners,
            BuildOptions options)
        {
            _buildFolders = buildFolders;
            _compilerRunners = compilerRunners;
            _options = options;

            _frameworkCompilationFailureBuckets = new Buckets();
            _compilationFailureBuckets = new Buckets();
            _executionFailureBuckets = new Buckets();
        }

        private void WriteJittedMethodSummary(StreamWriter logWriter)
        {
            Dictionary<string, HashSet<string>>[] allMethodsPerModulePerCompiler = new Dictionary<string, HashSet<string>>[(int)CompilerIndex.Count];

            foreach (CompilerRunner runner in _compilerRunners)
            {
                allMethodsPerModulePerCompiler[(int)runner.Index] = new Dictionary<string, HashSet<string>>();
            }

            foreach (BuildFolder folder in FoldersToBuild)
            {
                for (int exeIndex = 0; exeIndex < folder.Executions.Count; exeIndex++)
                {
                    Dictionary<string, HashSet<string>>[] appMethodsPerModulePerCompiler = new Dictionary<string, HashSet<string>>[(int)CompilerIndex.Count];
                    foreach (CompilerRunner runner in _compilerRunners)
                    {
                        appMethodsPerModulePerCompiler[(int)runner.Index] = new Dictionary<string, HashSet<string>>();
                        folder.AddModuleToJittedMethodsMapping(allMethodsPerModulePerCompiler[(int)runner.Index], exeIndex, runner.Index);
                        folder.AddModuleToJittedMethodsMapping(appMethodsPerModulePerCompiler[(int)runner.Index], exeIndex, runner.Index);
                    }
                    folder.WriteJitStatistics(appMethodsPerModulePerCompiler, _compilerRunners);
                }
            }

            BuildFolder.WriteJitStatistics(logWriter, allMethodsPerModulePerCompiler, _compilerRunners);
        }

        public bool Compile()
        {
            CompileFramework();

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            ResolveTestExclusions();

            List<ProcessInfo> compilationsToRun = new List<ProcessInfo>();

            foreach (BuildFolder folder in FoldersToBuild)
            {
                foreach (ProcessInfo[] compilation in folder.Compilations)
                {
                    foreach (CompilerRunner runner in _compilerRunners)
                    {
                        ProcessInfo compilationProcess = compilation[(int)runner.Index];
                        if (compilationProcess != null)
                        {
                            compilationsToRun.Add(compilationProcess);
                        }
                    }
                }
            }

            ParallelRunner.Run(compilationsToRun, _options.DegreeOfParallelism);
            
            bool success = true;
            List<KeyValuePair<string, string>> failedCompilationsPerBuilder = new List<KeyValuePair<string, string>>();
            int successfulCompileCount = 0;

            List<ProcessInfo> r2rDumpExecutionsToRun = new List<ProcessInfo>();

            foreach (BuildFolder folder in FoldersToBuild)
            {
                foreach (ProcessInfo[] compilation in folder.Compilations)
                {
                    string file = null;
                    string failedBuilders = null;
                    foreach (CompilerRunner runner in _compilerRunners)
                    {
                        ProcessInfo runnerProcess = compilation[(int)runner.Index];
                        if (runnerProcess == null)
                        {
                            // No runner process
                        }
                        else if (runnerProcess.Succeeded)
                        {
                            if (_options.R2RDumpPath != null)
                            {
                                r2rDumpExecutionsToRun.Add(new ProcessInfo(new R2RDumpProcessConstructor(runner, runnerProcess.Parameters.OutputFileName, naked: false)));
                                r2rDumpExecutionsToRun.Add(new ProcessInfo(new R2RDumpProcessConstructor(runner, runnerProcess.Parameters.OutputFileName, naked: true)));
                            }
                        }
                        else // runner process failed
                        {
                            _compilationFailureBuckets.AddCompilation(runnerProcess);
                            try
                            {
                                File.Copy(runnerProcess.Parameters.InputFileName, runnerProcess.Parameters.OutputFileName);
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine("Error copying {0} to {1}: {2}", runnerProcess.Parameters.InputFileName, runnerProcess.Parameters.OutputFileName, ex.Message);
                            }
                            if (file == null)
                            {
                                file = runnerProcess.Parameters.InputFileName;
                                failedBuilders = runner.CompilerName;
                            }
                            else
                            {
                                failedBuilders += "; " + runner.CompilerName;
                            }
                        }
                    }
                    if (file != null)
                    {
                        failedCompilationsPerBuilder.Add(new KeyValuePair<string, string>(file, failedBuilders));
                        success = false;
                    }
                    else
                    {
                        successfulCompileCount++;
                    }
                }
            }

            ParallelRunner.Run(r2rDumpExecutionsToRun, _options.DegreeOfParallelism);

            foreach (ProcessInfo r2rDumpExecution in r2rDumpExecutionsToRun)
            {
                if (!r2rDumpExecution.Succeeded)
                {
                    string causeOfFailure;
                    if (r2rDumpExecution.TimedOut)
                    {
                        causeOfFailure = "timed out";
                    }
                    else if (r2rDumpExecution.ExitCode != 0)
                    {
                        causeOfFailure = $"invalid exit code {r2rDumpExecution.ExitCode}";
                    }
                    else
                    {
                        causeOfFailure = "Unknown cause of failure";
                    }

                    Console.Error.WriteLine("Error running R2R dump on {0}: {1}", r2rDumpExecution.Parameters.InputFileName, causeOfFailure);
                    success = false;
                }
            }

            _compilationMilliseconds = stopwatch.ElapsedMilliseconds;

            return success;
        }

        public bool CompileFramework()
        {
            if (!_options.Framework)
            {
                return true;
            }

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            string coreRoot = _options.CoreRootDirectory.FullName;
            string[] frameworkFolderFiles = Directory.GetFiles(coreRoot);

            IEnumerable<CompilerRunner> frameworkRunners = _options.CompilerRunners(isFramework: true);

            // Pre-populate the output folders with the input files so that we have backdrops
            // for failing compilations.
            foreach (CompilerRunner runner in frameworkRunners)
            {
                string outputPath = runner.GetOutputPath(coreRoot);
                outputPath.RecreateDirectory();
            }

            List<ProcessInfo> compilationsToRun = new List<ProcessInfo>();
            List<KeyValuePair<string, ProcessInfo[]>> compilationsPerRunner = new List<KeyValuePair<string, ProcessInfo[]>>();
            foreach (string frameworkDll in ComputeManagedAssemblies.GetManagedAssembliesInFolder(_options.CoreRootDirectory.FullName))
            {
                ProcessInfo[] processes = new ProcessInfo[(int)CompilerIndex.Count];
                compilationsPerRunner.Add(new KeyValuePair<string, ProcessInfo[]>(frameworkDll, processes));
                foreach (CompilerRunner runner in frameworkRunners)
                {
                    ProcessInfo compilationProcess = new ProcessInfo(new CompilationProcessConstructor(runner, _options.CoreRootDirectory.FullName, frameworkDll));
                    compilationsToRun.Add(compilationProcess);
                    processes[(int)runner.Index] = compilationProcess;
                }
            }

            ParallelRunner.Run(compilationsToRun, _options.DegreeOfParallelism);

            HashSet<string> skipCopying = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int[] failedCompilationsPerBuilder = new int[(int)CompilerIndex.Count];
            int successfulCompileCount = 0;
            int failedCompileCount = 0;
            foreach (KeyValuePair<string, ProcessInfo[]> kvp in compilationsPerRunner)
            {
                bool anyCompilationsFailed = false;
                foreach (CompilerRunner runner in frameworkRunners)
                {
                    ProcessInfo compilationProcess = kvp.Value[(int)runner.Index];
                    if (compilationProcess.Succeeded)
                    {
                        skipCopying.Add(compilationProcess.Parameters.InputFileName);
                    }
                    else
                    {
                        anyCompilationsFailed = true;
                        failedCompilationsPerBuilder[(int)runner.Index]++;
                        _frameworkCompilationFailureBuckets.AddCompilation(compilationProcess);
                    }
                }
                if (anyCompilationsFailed)
                {
                    failedCompileCount++;
                }
                else
                {
                    successfulCompileCount++;
                }
            }

            foreach (CompilerRunner runner in frameworkRunners)
            {
                string outputPath = runner.GetOutputPath(coreRoot);
                foreach (string file in frameworkFolderFiles)
                {
                    if (!skipCopying.Contains(file))
                    {
                        string targetFile = Path.Combine(outputPath, Path.GetFileName(file));
                        File.Copy(file, targetFile, overwrite: true);
                    }
                }
            }

            _frameworkCompilationMilliseconds = stopwatch.ElapsedMilliseconds;

            return failedCompileCount == 0;
        }

        public bool Execute()
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            List<ProcessInfo> executionsToRun = new List<ProcessInfo>();

            foreach (BuildFolder folder in FoldersToBuild)
            {
                AddBuildFolderExecutions(executionsToRun, folder, stopwatch);
            }

            ParallelRunner.Run(executionsToRun, degreeOfParallelism: _options.Sequential ? 1 : 0);

            List<KeyValuePair<string, string>> failedExecutionsPerBuilder = new List<KeyValuePair<string, string>>();

            int successfulExecuteCount = 0;

            bool success = true;
            foreach (BuildFolder folder in FoldersToBuild)
            {
                foreach (ProcessInfo[] execution in folder.Executions)
                {
                    string file = null;
                    string failedBuilders = null;
                    foreach (CompilerRunner runner in _compilerRunners)
                    {
                        ProcessInfo runnerProcess = execution[(int)runner.Index];
                        if (runnerProcess != null && !runnerProcess.Succeeded)
                        {
                            _executionFailureBuckets.AddExecution(runnerProcess);

                            if (file == null)
                            {
                                file = runnerProcess.Parameters.InputFileName;
                                failedBuilders = runner.CompilerName;
                            }
                            else
                            {
                                failedBuilders += "; " + runner.CompilerName;
                            }
                        }
                    }
                    if (file != null)
                    {
                        failedExecutionsPerBuilder.Add(new KeyValuePair<string, string>(file, failedBuilders));
                        success = false;
                    }
                    else
                    {
                        successfulExecuteCount++;
                    }
                }
            }

            _executionMilliseconds = stopwatch.ElapsedMilliseconds;

            return success;
        }

        public bool Build(IEnumerable<CompilerRunner> runners)
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            bool success = Compile();

            if (!_options.NoExe)
            {
                success = Execute() && success;
            }

            _buildMilliseconds = stopwatch.ElapsedMilliseconds;

            return success;
        }

        private void ResolveTestExclusions()
        {
            TestExclusionMap exclusions = TestExclusionMap.Create(_options);
            foreach (BuildFolder folder in _buildFolders)
            {
                if (exclusions.TryGetIssue(folder.InputFolder, out string issueID))
                {
                    folder.IssueID = issueID;
                    continue;
                }
            }
        }

        private void AddBuildFolderExecutions(List<ProcessInfo> executionsToRun, BuildFolder folder, Stopwatch stopwatch)
        {
            foreach (ProcessInfo[] execution in folder.Executions)
            {
                foreach (CompilerRunner runner in _compilerRunners)
                {
                    ProcessInfo executionProcess = execution[(int)runner.Index];
                    if (executionProcess != null)
                    {
                        bool compilationsSucceeded = folder.Compilations.All(comp => comp[(int)runner.Index]?.Succeeded ?? true);
                        if (compilationsSucceeded)
                        {
                            executionsToRun.Add(executionProcess);
                        }
                        else
                        {
                            // Forget the execution process when compilation failed
                            execution[(int)runner.Index] = null;
                        }
                    }
                }
            }
        }

        private void WriteTopRankingProcesses(StreamWriter logWriter, string metric, IEnumerable<ProcessInfo> processes)
        {
            const int TopAppCount = 10;

            IEnumerable<ProcessInfo> selection = processes.OrderByDescending(process => process.DurationMilliseconds).Take(TopAppCount);
            int count = selection.Count();
            if (count == 0)
            {
                // No entries to log
                return;
            }

            logWriter.WriteLine();

            string headerLine = $"{count} top ranking {metric}";
            logWriter.WriteLine(headerLine);
            logWriter.WriteLine(new string('-', headerLine.Length));

            foreach (ProcessInfo processInfo in selection)
            {
                logWriter.WriteLine($"{processInfo.DurationMilliseconds,10} | {processInfo.Parameters.InputFileName}");
            }
        }

        enum CompilationOutcome
        {
            PASS = 0,
            FAIL = 1,

            Count
        }

        private enum ExecutionOutcome
        {
            PASS = 0,
            EXIT_CODE = 1,
            CRASHED = 2,
            TIMED_OUT = 3,

            Count
        }

        private void WriteBuildStatistics(StreamWriter logWriter)
        {
            // The Count'th element corresponds to totals over all compiler runners used in the run
            int[,] compilationOutcomes = new int[(int)CompilationOutcome.Count, (int)CompilerIndex.Count + 1];
            int[,] executionOutcomes = new int[(int)ExecutionOutcome.Count, (int)CompilerIndex.Count + 1];
            int totalCompilations = 0;
            int totalExecutions = 0;

            foreach (BuildFolder folder in FoldersToBuild)
            {
                bool[] compilationFailedPerRunner = new bool[(int)CompilerIndex.Count];
                foreach (ProcessInfo[] compilation in folder.Compilations)
                {
                    totalCompilations++;
                    bool anyCompilationFailed = false;
                    foreach (CompilerRunner runner in _compilerRunners)
                    {
                        bool compilationFailed = compilation[(int)runner.Index] != null && !compilation[(int)runner.Index].Succeeded;
                        if (compilationFailed)
                        {
                            compilationOutcomes[(int)CompilationOutcome.FAIL, (int)runner.Index]++;
                            anyCompilationFailed = true;
                            compilationFailedPerRunner[(int)runner.Index] = true;
                        }
                        else
                        {
                            compilationOutcomes[(int)CompilationOutcome.PASS, (int)runner.Index]++;
                        }
                    }
                    if (anyCompilationFailed)
                    {
                        compilationOutcomes[(int)CompilationOutcome.FAIL, (int)CompilerIndex.Count]++;
                    }
                    else
                    {
                        compilationOutcomes[(int)CompilationOutcome.PASS, (int)CompilerIndex.Count]++;
                    }
                }

                foreach (ProcessInfo[] execution in folder.Executions)
                {
                    totalExecutions++;
                    bool anyCompilationFailed = false;
                    int executionFailureOutcomeMask = 0;
                    foreach (CompilerRunner runner in _compilerRunners)
                    {
                        ProcessInfo execProcess = execution[(int)runner.Index];
                        bool compilationFailed = compilationFailedPerRunner[(int)runner.Index];
                        anyCompilationFailed |= compilationFailed;
                        bool executionFailed = !compilationFailed && (execProcess != null && !execProcess.Succeeded);
                        if (executionFailed)
                        {
                            ExecutionOutcome outcome = (execProcess.TimedOut ? ExecutionOutcome.TIMED_OUT :
                                execProcess.ExitCode < -1000 * 1000 ? ExecutionOutcome.CRASHED :
                                ExecutionOutcome.EXIT_CODE);
                            executionOutcomes[(int)outcome, (int)runner.Index]++;
                            executionFailureOutcomeMask |= 1 << (int)outcome;
                        }
                        if (!compilationFailed && !executionFailed)
                        {
                            executionOutcomes[(int)ExecutionOutcome.PASS, (int)runner.Index]++;
                        }
                    }
                    if (executionFailureOutcomeMask != 0)
                    {
                        for (int outcomeIndex = 0; outcomeIndex < (int)ExecutionOutcome.Count; outcomeIndex++)
                        {
                            if ((executionFailureOutcomeMask & (1 << outcomeIndex)) != 0)
                            {
                                executionOutcomes[outcomeIndex, (int)CompilerIndex.Count]++;
                            }
                        }
                    }
                    else
                    {
                        executionOutcomes[(int)ExecutionOutcome.PASS, (int)CompilerIndex.Count]++;
                    }
                }
            }

            logWriter.WriteLine();
            logWriter.WriteLine($"Configuration:    {(_options.Release ? "Release" : "Debug")}");
            logWriter.WriteLine($"Framework:        {(_options.Framework ? "build native" : _options.UseFramework ? "prebuilt native" : "MSIL")}");
            logWriter.WriteLine($"Version bubble:   {(_options.LargeBubble ? "input + all reference assemblies" : "single assembly")}");
            logWriter.WriteLine($"Input folder:     {_options.InputDirectory?.FullName}");
            logWriter.WriteLine($"CORE_ROOT:        {_options.CoreRootDirectory?.FullName}");
            logWriter.WriteLine($"CPAOT:            {_options.CpaotDirectory?.FullName}");
            logWriter.WriteLine($"Total folders:    {_buildFolders.Count()}");
            logWriter.WriteLine($"Blocked w/issues: {_buildFolders.Count(folder => folder.IsBlockedWithIssue)}");
            int foldersToBuild = FoldersToBuild.Count();
            logWriter.WriteLine($"Folders to build: {foldersToBuild}");
            logWriter.WriteLine($"# compilations:   {totalCompilations}");
            logWriter.WriteLine($"# executions:     {totalExecutions}");
            logWriter.WriteLine($"Total build time: {_buildMilliseconds} msecs");
            logWriter.WriteLine($"Framework time:   {_frameworkCompilationMilliseconds} msecs");
            logWriter.WriteLine($"Compilation time: {_compilationMilliseconds} msecs");
            logWriter.WriteLine($"Execution time:   {_executionMilliseconds} msecs");

            if (foldersToBuild != 0)
            {
                logWriter.WriteLine();
                logWriter.Write($"{totalCompilations,7} ILC |");
                foreach (CompilerRunner runner in _compilerRunners)
                {
                    logWriter.Write($"{runner.CompilerName,8} |");
                }
                logWriter.WriteLine(" Overall");
                int lineSize = 10 * _compilerRunners.Count() + 13 + 8;
                string separator = new string('-', lineSize);
                logWriter.WriteLine(separator);
                for (int outcomeIndex = 0; outcomeIndex < (int)CompilationOutcome.Count; outcomeIndex++)
                {
                    logWriter.Write($"{((CompilationOutcome)outcomeIndex).ToString(),11} |");
                    foreach (CompilerRunner runner in _compilerRunners)
                    {
                        logWriter.Write($"{compilationOutcomes[outcomeIndex, (int)runner.Index],8} |");
                    }
                    logWriter.WriteLine($"{compilationOutcomes[outcomeIndex, (int)CompilerIndex.Count],8}");
                }

                if (!_options.NoExe)
                {
                    logWriter.WriteLine();
                    logWriter.Write($"{totalExecutions,7} EXE |");
                    foreach (CompilerRunner runner in _compilerRunners)
                    {
                        logWriter.Write($"{runner.CompilerName,8} |");
                    }
                    logWriter.WriteLine(" Overall");
                    logWriter.WriteLine(separator);
                    for (int outcomeIndex = 0; outcomeIndex < (int)ExecutionOutcome.Count; outcomeIndex++)
                    {
                        logWriter.Write($"{((ExecutionOutcome)outcomeIndex).ToString(),11} |");
                        foreach (CompilerRunner runner in _compilerRunners)
                        {
                            logWriter.Write($"{executionOutcomes[outcomeIndex, (int)runner.Index],8} |");
                        }
                        logWriter.WriteLine($"{executionOutcomes[outcomeIndex, (int)CompilerIndex.Count],8}");
                    }
                }

                WritePerFolderStatistics(logWriter);

                WriteExecutableSizeStatistics(logWriter);

                WriteJittedMethodSummary(logWriter);

                WriteTopRankingProcesses(logWriter, "compilations by duration", EnumerateCompilations());
                WriteTopRankingProcesses(logWriter, "executions by duration", EnumerateExecutions());
            }

            if (_options.Framework)
            {
                logWriter.WriteLine();
                logWriter.WriteLine("Framework compilation failures:");
                FrameworkCompilationFailureBuckets.WriteToStream(logWriter, detailed: false);
            }

            if (foldersToBuild != 0)
            {
                logWriter.WriteLine();
                logWriter.WriteLine("Compilation failures:");
                CompilationFailureBuckets.WriteToStream(logWriter, detailed: false);

                if (!_options.NoExe)
                {
                    logWriter.WriteLine();
                    logWriter.WriteLine("Execution failures:");
                    ExecutionFailureBuckets.WriteToStream(logWriter, detailed: false);
                }
            }

            WriteFoldersBlockedWithIssues(logWriter);
        }

        private void WritePerFolderStatistics(StreamWriter logWriter)
        {
            string baseFolder = _options.InputDirectory.FullName;
            int baseOffset = baseFolder.Length + (baseFolder.Length > 0 && baseFolder[baseFolder.Length - 1] == Path.DirectorySeparatorChar ? 0 : 1);
            HashSet<string> folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BuildFolder folder in FoldersToBuild)
            {
                string relativeFolder = "";
                if (folder.InputFolder.Length > baseFolder.Length)
                {
                    relativeFolder = folder.InputFolder.Substring(baseOffset);
                }
                int endPos = relativeFolder.IndexOf(Path.DirectorySeparatorChar);
                if (endPos < 0)
                {
                    endPos = relativeFolder.Length;
                }
                folders.Add(relativeFolder.Substring(0, endPos));
            }
            if (folders.Count <= 1)
            {
                // Just one folder - no per folder statistics needed
                return;
            }

            List<string> folderList = new List<string>(folders);
            folderList.Sort(StringComparer.OrdinalIgnoreCase);
            logWriter.WriteLine();
            logWriter.WriteLine("Folder statistics:");
            logWriter.WriteLine("#ILC | PASS | FAIL | #EXE | PASS | FAIL | PATH");
            logWriter.WriteLine("----------------------------------------------");

            foreach (string relativeFolder in folderList)
            {
                string folder = Path.Combine(baseFolder, relativeFolder);
                int ilcCount = 0;
                int exeCount = 0;
                int exeFail = 0;
                int ilcFail = 0;
                foreach (BuildFolder buildFolder in FoldersToBuild)
                {
                    string buildFolderPath = buildFolder.InputFolder;
                    if (buildFolderPath.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                        buildFolderPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
                            buildFolderPath[folder.Length] == Path.DirectorySeparatorChar)
                    {
                        foreach (ProcessInfo[] compilation in buildFolder.Compilations)
                        {
                            bool anyIlcFail = false;
                            foreach (CompilerRunner runner in _compilerRunners)
                            {
                                if (compilation[(int)runner.Index] != null && !compilation[(int)runner.Index].Succeeded)
                                {
                                    anyIlcFail = true;
                                    break;
                                }
                            }
                            ilcCount++;
                            if (anyIlcFail)
                            {
                                ilcFail++;
                            }
                        }
                        foreach (ProcessInfo[] execution in buildFolder.Executions)
                        {
                            bool anyExeFail = false;
                            foreach (CompilerRunner runner in _compilerRunners)
                            {
                                if (execution[(int)runner.Index] != null && !execution[(int)runner.Index].Succeeded)
                                {
                                    anyExeFail = true;
                                    break;
                                }
                            }
                            exeCount++;
                            if (anyExeFail)
                            {
                                exeFail++;
                            }
                        }
                    }
                }
                logWriter.WriteLine($"{ilcCount,4} | {(ilcCount - ilcFail),4} | {ilcFail,4} | {exeCount,4} | {(exeCount - exeFail),4} | {exeFail,4} | {relativeFolder}");
            }
        }

        class ExeSizeInfo
        {
            public readonly string CpaotPath;
            public readonly long CpaotSize;
            public readonly string CrossgenPath;
            public readonly long CrossgenSize;

            public ExeSizeInfo(string cpaotPath, long cpaotSize, string crossgenPath, long crossgenSize)
            {
                CpaotPath = cpaotPath;
                CpaotSize = cpaotSize;
                CrossgenPath = crossgenPath;
                CrossgenSize = crossgenSize;
            }
        }

        private void WriteExecutableSizeStatistics(StreamWriter logWriter)
        {
            List<ExeSizeInfo> sizeStats = new List<ExeSizeInfo>();
            HashSet<string> libraryHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (BuildFolder folder in FoldersToBuild)
            {
                foreach (ProcessInfo[] compilation in folder.Compilations)
                {
                    ProcessInfo crossgenCompilation = compilation[(int)CompilerIndex.Crossgen];
                    ProcessInfo cpaotCompilation = compilation[(int)CompilerIndex.CPAOT];
                    if ((crossgenCompilation?.Succeeded ?? false) &&
                        (cpaotCompilation?.Succeeded ?? false))
                    {
                        long cpaotSize = new FileInfo(cpaotCompilation.Parameters.OutputFileName).Length;
                        long crossgenSize = new FileInfo(crossgenCompilation.Parameters.OutputFileName).Length;

                        string ext = Path.GetExtension(cpaotCompilation.Parameters.OutputFileName).ToLower();
                        if (ext == ".dll" || ext == ".so")
                        {
                            string hash = $"{Path.GetFileName(cpaotCompilation.Parameters.OutputFileName)}#{cpaotSize}#{crossgenSize}";
                            if (!libraryHashes.Add(hash))
                            {
                                // We ignore libraries with the same "simple name" if it has the same compiled size as many tests
                                // use support libraries that get separately compiled into their respective folders but semantically
                                // are "the same thing" so it doesn't make too much sense to report them multiple times.
                                continue;
                            }
                        }

                        sizeStats.Add(new ExeSizeInfo(
                            cpaotPath: cpaotCompilation.Parameters.OutputFileName,
                            cpaotSize: cpaotSize,
                            crossgenPath: crossgenCompilation.Parameters.OutputFileName,
                            crossgenSize: crossgenSize));

                    }
                }
            }

            if (sizeStats.Count == 0)
            {
                return;
            }

            long totalCpaotSize = sizeStats.Sum((stat) => stat.CpaotSize);
            long totalCrossgenSize = sizeStats.Sum((stat) => stat.CrossgenSize);

            const double MegaByte = 1024 * 1024;
            double KiloCount = 1024 * sizeStats.Count;

            logWriter.WriteLine();
            logWriter.WriteLine("Executable size statistics:");
            logWriter.WriteLine("Total CPAOT size:    {0:F3} MB ({1:F3} KB per app on average)", totalCpaotSize / MegaByte, totalCpaotSize / KiloCount);
            logWriter.WriteLine("Total Crossgen size: {0:F3} MB ({1:F3} KB per app on average)", totalCrossgenSize / MegaByte, totalCrossgenSize / KiloCount);

            long deltaSize = totalCpaotSize - totalCrossgenSize;
            logWriter.WriteLine("CPAOT - Crossgen:    {0:F3} MB ({1:F3} KB per app on average)", deltaSize / MegaByte, deltaSize / KiloCount);

            double percentageSizeRatio = totalCpaotSize * 100.0 / Math.Max(totalCrossgenSize, 1);
            logWriter.WriteLine("CPAOT / Crossgen:    {0:F3}%", percentageSizeRatio);

            sizeStats.Sort((a, b) => (b.CpaotSize - b.CrossgenSize).CompareTo(a.CpaotSize - a.CrossgenSize));

            const int TopExeCount = 10;

            int topCount;
            int bottomCount;
            
            if (sizeStats.Count <= 2 * TopExeCount)
            {
                topCount = sizeStats.Count;
                bottomCount = 0;
            }
            else
            {
                topCount = TopExeCount;
                bottomCount = TopExeCount;
            }

            logWriter.WriteLine();
            logWriter.WriteLine("CPAOT size |   Crossgen | CPAOT - CG | Highest exe size deltas");
            logWriter.WriteLine("--------------------------------------------------------------");
            foreach (ExeSizeInfo exeSize in sizeStats.Take(topCount))
            {
                logWriter.WriteLine(
                    "{0,10} | {1,10} | {2,10} | {3}",
                    exeSize.CpaotSize,
                    exeSize.CrossgenSize,
                    exeSize.CpaotSize - exeSize.CrossgenSize,
                    exeSize.CpaotPath);
            }

            if (bottomCount > 0)
            {
                logWriter.WriteLine();
                logWriter.WriteLine("CPAOT size |   Crossgen | CPAOT - CG | Lowest exe size deltas");
                logWriter.WriteLine("-------------------------------------------------------------");
                foreach (ExeSizeInfo exeSize in sizeStats.TakeLast(bottomCount))
                {
                    logWriter.WriteLine(
                        "{0,10} | {1,10} | {2,10} | {3}",
                        exeSize.CpaotSize,
                        exeSize.CrossgenSize,
                        exeSize.CpaotSize - exeSize.CrossgenSize,
                        exeSize.CpaotPath);
                }
            }

            sizeStats.Sort((a, b) => (b.CpaotSize * a.CrossgenSize).CompareTo(a.CpaotSize * b.CrossgenSize));

            logWriter.WriteLine();
            logWriter.WriteLine("CPAOT size |   Crossgen | CPAOT/CG % | Highest exe size ratios");
            logWriter.WriteLine("--------------------------------------------------------------");
            foreach (ExeSizeInfo exeSize in sizeStats.Take(topCount))
            {
                logWriter.WriteLine(
                    "{0,10} | {1,10} | {2,10:F3} | {3}",
                    exeSize.CpaotSize,
                    exeSize.CrossgenSize,
                    exeSize.CpaotSize * 100.0 / exeSize.CrossgenSize,
                    exeSize.CpaotPath);
            }

            if (bottomCount > 0)
            {
                logWriter.WriteLine();
                logWriter.WriteLine("CPAOT size |   Crossgen | CPAOT/CG % | Lowest exe size ratios");
                logWriter.WriteLine("-------------------------------------------------------------");
                foreach (ExeSizeInfo exeSize in sizeStats.TakeLast(bottomCount))
                {
                    logWriter.WriteLine(
                        "{0,10} | {1,10} | {2,10:F6} | {3}",
                        exeSize.CpaotSize,
                        exeSize.CrossgenSize,
                        exeSize.CpaotSize * 100.0 / exeSize.CrossgenSize,
                        exeSize.CpaotPath);
                }
            }
        }

        private IEnumerable<ProcessInfo> EnumerateCompilations()
        {
            foreach (BuildFolder folder in FoldersToBuild)
            {
                foreach (ProcessInfo[] compilation in folder.Compilations)
                {
                    foreach (CompilerRunner runner in _compilerRunners)
                    {
                        ProcessInfo compilationProcess = compilation[(int)runner.Index];
                        if (compilationProcess != null)
                        {
                            yield return compilationProcess;
                        }
                    }
                }
            }
        }

        private IEnumerable<ProcessInfo> EnumerateExecutions()
        {
            foreach (BuildFolder folder in FoldersToBuild)
            {
                foreach (ProcessInfo[] execution in folder.Executions)
                {
                    foreach (CompilerRunner runner in _compilerRunners)
                    {
                        ProcessInfo executionProcess = execution[(int)runner.Index];
                        if (executionProcess != null)
                        {
                            yield return executionProcess;
                        }
                    }
                }
            }
        }

        public void WriteBuildLog(string buildLogPath)
        {
            using (StreamWriter buildLogWriter = new StreamWriter(buildLogPath))
            {
                WriteBuildStatistics(buildLogWriter);
            }
        }

        public void WriteCombinedLog(string outputFile)
        {
            using (StreamWriter combinedLog = new StreamWriter(outputFile))
            {
                StreamWriter[] perRunnerLog = new StreamWriter[(int)CompilerIndex.Count];
                foreach (CompilerRunner runner in _compilerRunners)
                {
                    string runnerLogPath = Path.ChangeExtension(outputFile, "-" + runner.CompilerName + ".log");
                    perRunnerLog[(int)runner.Index] = new StreamWriter(runnerLogPath);
                }

                foreach (BuildFolder folder in FoldersToBuild)
                {
                    bool[] compilationErrorPerRunner = new bool[(int)CompilerIndex.Count];
                    foreach (ProcessInfo[] compilation in folder.Compilations)
                    {
                        foreach (CompilerRunner runner in _compilerRunners)
                        {
                            ProcessInfo compilationProcess = compilation[(int)runner.Index];
                            if (compilationProcess != null)
                            {
                                string log = $"\nCOMPILE {runner.CompilerName}:{compilationProcess.Parameters.InputFileName}\n" + File.ReadAllText(compilationProcess.Parameters.LogPath);
                                perRunnerLog[(int)runner.Index].Write(log);
                                combinedLog.Write(log);
                                if (!compilationProcess.Succeeded)
                                {
                                    compilationErrorPerRunner[(int)runner.Index] = true;
                                }
                            }
                        }
                    }
                    foreach (ProcessInfo[] execution in folder.Executions)
                    {
                        foreach (CompilerRunner runner in _compilerRunners)
                        {
                            if (!compilationErrorPerRunner[(int)runner.Index])
                            {
                                ProcessInfo executionProcess = execution[(int)runner.Index];
                                if (executionProcess != null)
                                {
                                    string log = $"\nEXECUTE {runner.CompilerName}:{executionProcess.Parameters.InputFileName}\n";
                                    try
                                    {
                                        log += File.ReadAllText(executionProcess.Parameters.LogPath);
                                    }
                                    catch (Exception ex)
                                    {
                                        log += " -> " + ex.Message;
                                    }
                                    perRunnerLog[(int)runner.Index].Write(log);
                                    combinedLog.Write(log);
                                }
                            }
                        }
                    }
                }

                foreach (CompilerRunner runner in _compilerRunners)
                {
                    perRunnerLog[(int)runner.Index].Dispose();
                }
            }
        }

        private void WriteFoldersBlockedWithIssues(StreamWriter logWriter)
        {
            IEnumerable<BuildFolder> blockedFolders = _buildFolders.Where(folder => folder.IsBlockedWithIssue);

            int blockedCount = blockedFolders.Count();

            logWriter.WriteLine();
            logWriter.WriteLine($"Folders blocked with issues ({blockedCount} total):");
            logWriter.WriteLine("ISSUE | TEST");
            logWriter.WriteLine("------------");
            foreach (BuildFolder folder in blockedFolders)
            {
                logWriter.WriteLine($"{folder.IssueID,5} | {folder.InputFolder}");
            }
        }

        public void WriteLogs()
        {
            string timestamp = DateTime.Now.ToString("MMdd-HHmm");

            string suffix = (_options.Release ? "ret-" : "chk-") + timestamp + ".log";

            string buildLogPath = Path.Combine(_options.OutputDirectory.FullName, "build-" + suffix);
            WriteBuildLog(buildLogPath);

            string combinedSetLogPath = Path.Combine(_options.OutputDirectory.FullName, "combined-" + suffix);
            WriteCombinedLog(combinedSetLogPath);

            string frameworkBucketsFile = Path.Combine(_options.OutputDirectory.FullName, "framework-buckets-" + suffix);
            FrameworkCompilationFailureBuckets.WriteToFile(frameworkBucketsFile, detailed: true);

            string compilationBucketsFile = Path.Combine(_options.OutputDirectory.FullName, "compilation-buckets-" + suffix);
            CompilationFailureBuckets.WriteToFile(compilationBucketsFile, detailed: true);

            string executionBucketsFile = Path.Combine(_options.OutputDirectory.FullName, "execution-buckets-" + suffix);
            ExecutionFailureBuckets.WriteToFile(executionBucketsFile, detailed: true);
        }

        public IEnumerable<BuildFolder> FoldersToBuild => _buildFolders.Where(folder => !folder.IsBlockedWithIssue);

        public Buckets FrameworkCompilationFailureBuckets => _frameworkCompilationFailureBuckets;

        public Buckets CompilationFailureBuckets => _compilationFailureBuckets;

        public Buckets ExecutionFailureBuckets => _executionFailureBuckets;
    }
}
