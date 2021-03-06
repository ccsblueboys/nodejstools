// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.NodejsTools.TestAdapter.TestFrameworks;
using Microsoft.VisualStudio.Telemetry;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using Microsoft.VisualStudioTools;
using Microsoft.VisualStudioTools.Project;
using Newtonsoft.Json;
using MSBuild = Microsoft.Build.Evaluation;

namespace Microsoft.NodejsTools.TestAdapter
{
    internal class TestExecutionRedirector : Redirector
    {
        Action<string> writer;
        public TestExecutionRedirector(Action<string> onWriteLine)
        {
            writer = onWriteLine;
        }
        public override void WriteErrorLine(string line)
        {
            writer(line);
        }

        public override void WriteLine(string line)
        {
            writer(line);
        }

        public override bool CloseStandardInput()
        {
            return false;
        }
    }

    public class TestExecutor
    {
        public const string ExecutorUriString = "executor://NodejsTestExecutor/v1";
        public static readonly Uri ExecutorUri = new Uri(ExecutorUriString);
        //get from NodeRemoteDebugPortSupplier::PortSupplierId
        private static readonly Guid NodejsRemoteDebugPortSupplierUnsecuredId = new Guid("{9E16F805-5EFC-4CE5-8B67-9AE9B643EF80}");

        private readonly ManualResetEvent _cancelRequested = new ManualResetEvent(false);

        private static readonly char[] _needToBeQuoted = new[] { ' ', '"' };
        private ProcessOutput _nodeProcess;
        private object _syncObject = new object();
        private List<TestCase> _currentTests;
        private IFrameworkHandle _frameworkHandle;
        private TestResult _currentResult = null;
        private ResultObject _currentResultObject = null;

        public void Cancel()
        {
            //let us just kill the node process there, rather do it late, because VS engine process 
            //could exit right after this call and our node process will be left running.
            KillNodeProcess();
            _cancelRequested.Set();
        }

        private void ProcessTestRunnerEmit(string line)
        {
            try
            {
                var testEvent = JsonConvert.DeserializeObject<TestEvent>(line);
                // Extract test from list of tests
                var tests = _currentTests.Where(n => n.DisplayName == testEvent.title);
                if (tests.Count() > 0)
                {
                    switch (testEvent.type)
                    {
                        case "test start":
                            {
                                _currentResult = new TestResult(tests.First());
                                _currentResult.StartTime = DateTimeOffset.Now;
                                _frameworkHandle.RecordStart(tests.First());
                            }
                            break;
                        case "result":
                            {
                                RecordEnd(_frameworkHandle, tests.First(), _currentResult, testEvent.result);
                            }
                            break;
                        case "pending":
                            {
                                _currentResult = new TestResult(tests.First());
                                RecordEnd(_frameworkHandle, tests.First(), _currentResult, testEvent.result);
                            }
                            break;
                    }
                }
                else if (testEvent.type == "suite end")
                {
                    _currentResultObject = testEvent.result;
                }
            }
            catch (JsonReaderException)
            {
                // Often lines emitted while running tests are not test results, and thus will fail to parse above
            }
        }

        /// <summary>
        /// This is the equivalent of "RunAll" functionality
        /// </summary>
        /// <param name="sources">Refers to the list of test sources passed to the test adapter from the client.  (Client could be VS or command line)</param>
        /// <param name="runContext">Defines the settings related to the current run</param>
        /// <param name="frameworkHandle">Handle to framework.  Used for recording results</param>
        public void RunTests(IEnumerable<string> sources, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            ValidateArg.NotNull(sources, "sources");
            ValidateArg.NotNull(runContext, "runContext");
            ValidateArg.NotNull(frameworkHandle, "frameworkHandle");

            _cancelRequested.Reset();

            var receiver = new TestReceiver();
            var discoverer = new TestDiscoverer();
            discoverer.DiscoverTests(sources, null, frameworkHandle, receiver);

            if (_cancelRequested.WaitOne(0))
            {
                return;
            }

            RunTests(receiver.Tests, runContext, frameworkHandle);
        }

        /// <summary>
        /// This is the equivalent of "Run Selected Tests" functionality.
        /// </summary>
        /// <param name="tests">The list of TestCases selected to run</param>
        /// <param name="runContext">Defines the settings related to the current run</param>
        /// <param name="frameworkHandle">Handle to framework.  Used for recording results</param>
        public void RunTests(IEnumerable<TestCase> tests, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            ValidateArg.NotNull(tests, "tests");
            ValidateArg.NotNull(runContext, "runContext");
            ValidateArg.NotNull(frameworkHandle, "frameworkHandle");
            _cancelRequested.Reset();

            // .ts file path -> project settings
            var fileToTests = new Dictionary<string, List<TestCase>>();
            var sourceToSettings = new Dictionary<string, NodejsProjectSettings>();
            NodejsProjectSettings projectSettings = null;

            // put tests into dictionary where key is their source file
            foreach (var test in tests)
            {
                if (!fileToTests.ContainsKey(test.CodeFilePath))
                {
                    fileToTests[test.CodeFilePath] = new List<TestCase>();
                }
                fileToTests[test.CodeFilePath].Add(test);
            }

            // where key is the file and value is a list of tests
            foreach (var entry in fileToTests)
            {
                var firstTest = entry.Value.ElementAt(0);
                if (!sourceToSettings.TryGetValue(firstTest.Source, out projectSettings))
                {
                    sourceToSettings[firstTest.Source] = projectSettings = LoadProjectSettings(firstTest.Source);
                }

                _currentTests = entry.Value;
                _frameworkHandle = frameworkHandle;

                // Run all test cases in a given file
                RunTestCases(entry.Value, runContext, frameworkHandle, projectSettings);
            }
        }

        private void LogTelemetry(int testCount, Version nodeVersion, bool isDebugging)
        {
            var userTask = new UserTaskEvent("VS/NodejsTools/UnitTestsExecuted", TelemetryResult.Success);
            userTask.Properties["VS.NodejsTools.TestCount"] = testCount;
            // This is safe, since changes to the ToString method are very unlikely, as the current output is widely documented.
            userTask.Properties["VS.NodejsTools.NodeVersion"] = nodeVersion.ToString();
            userTask.Properties["VS.NodejsTools.IsDebugging"] = isDebugging;

            //todo: when we have support for the Node 8 debugger log which version of the debugger people are actually using

            var defaultSession = TelemetryService.DefaultSession;
            defaultSession.PostEvent(userTask);
        }

        private void RunTestCases(IEnumerable<TestCase> tests, IRunContext runContext, IFrameworkHandle frameworkHandle, NodejsProjectSettings settings)
        {
            // May be null, but this is handled by RunTestCase if it matters.
            // No VS instance just means no debugging, but everything else is
            // okay.
            if (tests.Count() == 0)
            {
                return;
            }

            using (var app = VisualStudioApp.FromEnvironmentVariable(NodejsConstants.NodeToolsProcessIdEnvironmentVariable))
            {
                var port = 0;
                var nodeArgs = new List<string>();
                // .njsproj file path -> project settings
                var sourceToSettings = new Dictionary<string, NodejsProjectSettings>();
                var testObjects = new List<TestCaseObject>();

                if (!File.Exists(settings.NodeExePath))
                {
                    frameworkHandle.SendMessage(TestMessageLevel.Error, "Interpreter path does not exist: " + settings.NodeExePath);
                    return;
                }

                // All tests being run are for the same test file, so just use the first test listed to get the working dir
                var testInfo = new NodejsTestInfo(tests.First().FullyQualifiedName);
                var workingDir = Path.GetDirectoryName(CommonUtils.GetAbsoluteFilePath(settings.WorkingDir, testInfo.ModulePath));

                var nodeVersion = Nodejs.GetNodeVersion(settings.NodeExePath);

                // We can only log telemetry when we're running in VS.
                // Since the required assemblies are not on disk if we're not running in VS, we have to reference them in a separate method
                // this way the .NET framework only tries to load the assemblies when we actually need them.
                if (app != null)
                {
                    LogTelemetry(tests.Count(), nodeVersion, runContext.IsBeingDebugged);
                }

                foreach (var test in tests)
                {
                    if (_cancelRequested.WaitOne(0))
                    {
                        break;
                    }

                    if (settings == null)
                    {
                        frameworkHandle.SendMessage(
                            TestMessageLevel.Error,
                            $"Unable to determine interpreter to use for {test.Source}.");
                        frameworkHandle.RecordEnd(test, TestOutcome.Failed);
                    }

                    var args = new List<string>();
                    args.AddRange(GetInterpreterArgs(test, workingDir, settings.ProjectRootDir));

                    // Fetch the run_tests argument for starting node.exe if not specified yet
                    if (nodeArgs.Count == 0 && args.Count > 0)
                    {
                        nodeArgs.Add(args[0]);
                    }

                    testObjects.Add(new TestCaseObject(args[1], args[2], args[3], args[4], args[5]));
                }

                if (runContext.IsBeingDebugged && app != null)
                {
                    app.GetDTE().Debugger.DetachAll();
                    // Ensure that --debug-brk is the first argument
                    nodeArgs.InsertRange(0, GetDebugArgs(out port));
                }

                _nodeProcess = ProcessOutput.Run(
                    settings.NodeExePath,
                    nodeArgs,
                    settings.WorkingDir,
                    /* env */        null,
                    /* visible */    false,
                    /* redirector */ new TestExecutionRedirector(this.ProcessTestRunnerEmit),
                    /* quote args */ false);

                if (runContext.IsBeingDebugged && app != null)
                {
                    try
                    {
                        //the '#ping=0' is a special flag to tell VS node debugger not to connect to the port,
                        //because a connection carries the consequence of setting off --debug-brk, and breakpoints will be missed.
                        var qualifierUri = string.Format("tcp://localhost:{0}#ping=0", port);
                        while (!app.AttachToProcess(_nodeProcess, NodejsRemoteDebugPortSupplierUnsecuredId, qualifierUri))
                        {
                            if (_nodeProcess.Wait(TimeSpan.FromMilliseconds(500)))
                            {
                                break;
                            }
                        }
#if DEBUG
                    }
                    catch (COMException ex)
                    {
                        frameworkHandle.SendMessage(TestMessageLevel.Error, "Error occurred connecting to debuggee.");
                        frameworkHandle.SendMessage(TestMessageLevel.Error, ex.ToString());
                        KillNodeProcess();
                    }
#else
                    } catch (COMException) {
                        frameworkHandle.SendMessage(TestMessageLevel.Error, "Error occurred connecting to debuggee.");
                        KillNodeProcess();
                    }
#endif
                }
                // Send the process the list of tests to run and wait for it to complete
                _nodeProcess.WriteInputLine(JsonConvert.SerializeObject(testObjects));
                _nodeProcess.Wait();

                // Automatically fail tests that haven't been run by this point (failures in before() hooks)
                foreach (var notRunTest in _currentTests)
                {
                    var result = new TestResult(notRunTest);
                    result.Outcome = TestOutcome.Failed;
                    if (_currentResultObject != null)
                    {
                        result.Messages.Add(new TestResultMessage(TestResultMessage.StandardOutCategory, _currentResultObject.stdout));
                        result.Messages.Add(new TestResultMessage(TestResultMessage.StandardErrorCategory, _currentResultObject.stderr));
                    }
                    frameworkHandle.RecordResult(result);
                    frameworkHandle.RecordEnd(notRunTest, TestOutcome.Failed);
                }
            }
        }

        private void KillNodeProcess()
        {
            lock (_syncObject)
            {
                if (_nodeProcess != null)
                {
                    _nodeProcess.Kill();
                }
            }
        }

        private static int GetFreePort()
        {
            return Enumerable.Range(new Random().Next(49152, 65536), 60000).Except(
                from connection in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
                select connection.LocalEndPoint.Port
            ).First();
        }

        private IEnumerable<string> GetInterpreterArgs(TestCase test, string workingDir, string projectRootDir)
        {
            var testInfo = new TestFrameworks.NodejsTestInfo(test.FullyQualifiedName);
            var discover = new TestFrameworks.FrameworkDiscover();
            return discover.Get(testInfo.TestFramework).ArgumentsToRunTests(testInfo.TestName, testInfo.ModulePath, workingDir, projectRootDir);
        }

        private static IEnumerable<string> GetDebugArgs(out int port)
        {
            port = GetFreePort();

            // TODO: Need to use --inspect-brk on Node.js 8 or later
            return new[] {
                "--debug-brk=" + port.ToString()
            };
        }

        private NodejsProjectSettings LoadProjectSettings(string projectFile)
        {
            var env = new Dictionary<string, string>();

            var root = Environment.GetEnvironmentVariable(NodejsConstants.NodeToolsVsInstallRootEnvironmentVariable);
            if (!string.IsNullOrEmpty(root))
            {
                env["VsInstallRoot"] = root;
                env["MSBuildExtensionsPath32"] = Path.Combine(root, "MSBuild");
            }

            var buildEngine = new MSBuild.ProjectCollection(env);
            var proj = buildEngine.LoadProject(projectFile);

            var projectRootDir = Path.GetFullPath(Path.Combine(proj.DirectoryPath, proj.GetPropertyValue(CommonConstants.ProjectHome) ?? "."));

            return new NodejsProjectSettings()
            {
                ProjectRootDir = projectRootDir,

                WorkingDir = Path.GetFullPath(Path.Combine(projectRootDir, proj.GetPropertyValue(CommonConstants.WorkingDirectory) ?? ".")),

                NodeExePath = Nodejs.GetAbsoluteNodeExePath(
                    projectRootDir,
                    proj.GetPropertyValue(NodeProjectProperty.NodeExePath))
            };
        }

        private void RecordEnd(IFrameworkHandle frameworkHandle, TestCase test, TestResult result, ResultObject resultObject)
        {
            string[] standardOutputLines = resultObject.stdout.Split('\n');
            string[] standardErrorLines = resultObject.stderr.Split('\n');

            if (null != resultObject.pending && (bool)resultObject.pending)
            {
                result.Outcome = TestOutcome.Skipped;
            }
            else
            {
                result.EndTime = DateTimeOffset.Now;
                result.Duration = result.EndTime - result.StartTime;
                result.Outcome = resultObject.passed ? TestOutcome.Passed : TestOutcome.Failed;
            }
            result.Messages.Add(new TestResultMessage(TestResultMessage.StandardOutCategory, string.Join(Environment.NewLine, standardOutputLines)));
            result.Messages.Add(new TestResultMessage(TestResultMessage.StandardErrorCategory, string.Join(Environment.NewLine, standardErrorLines)));
            result.Messages.Add(new TestResultMessage(TestResultMessage.AdditionalInfoCategory, string.Join(Environment.NewLine, standardErrorLines)));
            frameworkHandle.RecordResult(result);
            frameworkHandle.RecordEnd(test, result.Outcome);
            _currentTests.Remove(test);
        }
    }
}

internal class DataReceiver
{
    public readonly StringBuilder Data = new StringBuilder();

    public void DataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data != null)
        {
            Data.AppendLine(e.Data);
        }
    }
}

internal class TestReceiver : ITestCaseDiscoverySink
{
    public List<TestCase> Tests { get; private set; }

    public TestReceiver()
    {
        Tests = new List<TestCase>();
    }

    public void SendTestCase(TestCase discoveredTest)
    {
        Tests.Add(discoveredTest);
    }
}

internal class NodejsProjectSettings
{
    public NodejsProjectSettings()
    {
        NodeExePath = string.Empty;
        SearchPath = string.Empty;
        WorkingDir = string.Empty;
    }

    public string NodeExePath { get; set; }
    public string SearchPath { get; set; }
    public string WorkingDir { get; set; }
    public string ProjectRootDir { get; set; }
}

internal class ResultObject
{
    public ResultObject()
    {
        title = string.Empty;
        passed = false;
        pending = false;
        stdout = string.Empty;
        stderr = string.Empty;
    }
    public string title { get; set; }
    public bool passed { get; set; }
    public bool? pending { get; set; }
    public string stdout { get; set; }
    public string stderr { get; set; }
}

internal class TestEvent
{
    public string type { get; set; }
    public string title { get; set; }
    public ResultObject result { get; set; }
}

internal class TestCaseObject
{
    public TestCaseObject()
    {
        framework = string.Empty;
        testName = string.Empty;
        testFile = string.Empty;
        workingFolder = string.Empty;
        projectFolder = string.Empty;
    }

    public TestCaseObject(string framework, string testName, string testFile, string workingFolder, string projectFolder)
    {
        this.framework = framework;
        this.testName = testName;
        this.testFile = testFile;
        this.workingFolder = workingFolder;
        this.projectFolder = projectFolder;
    }
    public string framework { get; set; }
    public string testName { get; set; }
    public string testFile { get; set; }
    public string workingFolder { get; set; }
    public string projectFolder { get; set; }
}
