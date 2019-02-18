using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Loader;
using System.Reflection;
using System.Collections.Generic;
using Microsoft.TeamFoundation.DistributedTask.Logging;
using System.Net.Http.Headers;

namespace Microsoft.VisualStudio.Services.Agent.Tests
{
    public sealed class TestHostContext : IHostContext, IDisposable
    {
        private readonly ConcurrentDictionary<Type, ConcurrentQueue<object>> _serviceInstances = new ConcurrentDictionary<Type, ConcurrentQueue<object>>();
        private readonly ConcurrentDictionary<Type, object> _serviceSingletons = new ConcurrentDictionary<Type, object>();
        private readonly ITraceManager _traceManager;
        private readonly Terminal _term;
        private readonly SecretMasker _secretMasker;
        private CancellationTokenSource _agentShutdownTokenSource = new CancellationTokenSource();
        private string _suiteName;
        private string _testName;
        private Tracing _trace;
        private AssemblyLoadContext _loadContext;
        private string _tempDirectoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("D"));
        private StartupType _startupType;
        public event EventHandler Unloading;
        public CancellationToken AgentShutdownToken => _agentShutdownTokenSource.Token;
        public ShutdownReason AgentShutdownReason { get; private set; }
        public ISecretMasker SecretMasker => _secretMasker;
        public TestHostContext(object testClass, [CallerMemberName] string testName = "")
        {
            ArgUtil.NotNull(testClass, nameof(testClass));
            ArgUtil.NotNullOrEmpty(testName, nameof(testName));
            _loadContext = AssemblyLoadContext.GetLoadContext(typeof(TestHostContext).GetTypeInfo().Assembly);
            _loadContext.Unloading += LoadContext_Unloading;
            _testName = testName;

            // Trim the test assembly's root namespace from the test class's full name.
            _suiteName = testClass.GetType().FullName.Substring(
                startIndex: typeof(Tests.TestHostContext).FullName.LastIndexOf(nameof(TestHostContext)));
            _suiteName = _suiteName.Replace(".", "_");

            // Setup the trace manager.
            TraceFileName = Path.Combine(
                Path.Combine(TestUtil.GetSrcPath(), "Test", "TestLogs"),
                $"trace_{_suiteName}_{_testName}.log");
            if (File.Exists(TraceFileName))
            {
                File.Delete(TraceFileName);
            }

            var traceListener = new HostTraceListener(TraceFileName);
            _secretMasker = new SecretMasker();
            _secretMasker.AddValueEncoder(ValueEncoders.JsonStringEscape);
            _secretMasker.AddValueEncoder(ValueEncoders.UriDataEscape);
            _traceManager = new TraceManager(traceListener, _secretMasker);
            _trace = GetTrace(nameof(TestHostContext));

            // inject a terminal in silent mode so all console output
            // goes to the test trace file
            _term = new Terminal();
            _term.Silent = true;
            SetSingleton<ITerminal>(_term);
            EnqueueInstance<ITerminal>(_term);

#if !OS_WINDOWS
            string eulaFile = Path.Combine(GetDirectory(WellKnownDirectory.Externals), Constants.Path.TeeDirectory, "license.html");
            Directory.CreateDirectory(GetDirectory(WellKnownDirectory.Externals));
            Directory.CreateDirectory(Path.Combine(GetDirectory(WellKnownDirectory.Externals), Constants.Path.TeeDirectory));
            File.WriteAllText(eulaFile, "testeulafile");
#endif
        }

        public CultureInfo DefaultCulture { get; private set; }

        public RunMode RunMode { get; set; }

        public string TraceFileName { get; private set; }

        public StartupType StartupType
        {
            get
            {
                return _startupType;
            }
            set
            {
                _startupType = value;
            }
        }

        public ProductInfoHeaderValue UserAgent => new ProductInfoHeaderValue("L0Test", "0.0");

        public async Task Delay(TimeSpan delay, CancellationToken token)
        {
            await Task.Delay(TimeSpan.Zero);
        }

        public T CreateService<T>() where T : class, IAgentService
        {
            _trace.Verbose($"Create service: '{typeof(T).Name}'");

            // Dequeue a registered instance.
            object service;
            ConcurrentQueue<object> queue;
            if (!_serviceInstances.TryGetValue(typeof(T), out queue) ||
                !queue.TryDequeue(out service))
            {
                throw new Exception($"Unable to dequeue a registered instance for type '{typeof(T).FullName}'.");
            }

            var s = service as T;
            s.Initialize(this);
            return s;
        }

        public T GetService<T>() where T : class, IAgentService
        {
            _trace.Verbose($"Get service: '{typeof(T).Name}'");

            // Get the registered singleton instance.
            object service;
            if (!_serviceSingletons.TryGetValue(typeof(T), out service))
            {
                throw new Exception($"Singleton instance not registered for type '{typeof(T).FullName}'.");
            }

            T s = service as T;
            s.Initialize(this);
            return s;
        }

        public void EnqueueInstance<T>(T instance) where T : class, IAgentService
        {
            // Enqueue a service instance to be returned by CreateService.
            if (object.ReferenceEquals(instance, null))
            {
                throw new ArgumentNullException(nameof(instance));
            }

            ConcurrentQueue<object> queue = _serviceInstances.GetOrAdd(
                key: typeof(T),
                valueFactory: x => new ConcurrentQueue<object>());
            queue.Enqueue(instance);
        }

        public void SetDefaultCulture(string name)
        {
            DefaultCulture = new CultureInfo(name);
        }

        public void SetSingleton<T>(T singleton) where T : class, IAgentService
        {
            // Set the singleton instance to be returned by GetService.
            if (object.ReferenceEquals(singleton, null))
            {
                throw new ArgumentNullException(nameof(singleton));
            }

            _serviceSingletons[typeof(T)] = singleton;
        }

        public string GetDirectory(WellKnownDirectory directory)
        {
            string path;
            switch (directory)
            {
                case WellKnownDirectory.Bin:
                    path = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
                    break;

                case WellKnownDirectory.Diag:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        Constants.Path.DiagDirectory);
                    break;

                case WellKnownDirectory.Externals:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        Constants.Path.ExternalsDirectory);
                    break;

                case WellKnownDirectory.LegacyPSHost:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Externals),
                        Constants.Path.LegacyPSHostDirectory);
                    break;

                case WellKnownDirectory.Root:
                    path = new DirectoryInfo(GetDirectory(WellKnownDirectory.Bin)).Parent.FullName;
                    break;

                case WellKnownDirectory.ServerOM:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Externals),
                        Constants.Path.ServerOMDirectory);
                    break;

                case WellKnownDirectory.Tee:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Externals),
                        Constants.Path.TeeDirectory);
                    break;

                case WellKnownDirectory.Temp:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Work),
                        Constants.Path.TempDirectory);
                    break;

                case WellKnownDirectory.Tasks:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Work),
                        Constants.Path.TasksDirectory);
                    break;

                case WellKnownDirectory.Tools:
                    path = Environment.GetEnvironmentVariable("AGENT_TOOLSDIRECTORY") ?? Environment.GetEnvironmentVariable(Constants.Variables.Agent.ToolsDirectory);
                    if (string.IsNullOrEmpty(path))
                    {
                        path = Path.Combine(
                            GetDirectory(WellKnownDirectory.Work),
                            Constants.Path.ToolDirectory);
                    }
                    break;

                case WellKnownDirectory.Update:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Work),
                        Constants.Path.UpdateDirectory);
                    break;

                case WellKnownDirectory.Work:
                    path = Path.Combine(
                        _tempDirectoryRoot,
                        WellKnownDirectory.Work.ToString());
                    break;

                default:
                    throw new NotSupportedException($"Unexpected well known directory: '{directory}'");
            }

            _trace.Info($"Well known directory '{directory}': '{path}'");
            return path;
        }

        public string GetConfigFile(WellKnownConfigFile configFile)
        {
            string path;
            switch (configFile)
            {
                case WellKnownConfigFile.Agent:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".agent");
                    break;

                case WellKnownConfigFile.Credentials:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".credentials");
                    break;

                case WellKnownConfigFile.RSACredentials:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".credentials_rsaparams");
                    break;

                case WellKnownConfigFile.Service:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".service");
                    break;

                case WellKnownConfigFile.CredentialStore:
#if OS_OSX
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".credential_store.keychain");
#else
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".credential_store");
#endif
                    break;

                case WellKnownConfigFile.Certificates:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".certificates");
                    break;

                case WellKnownConfigFile.Proxy:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".proxy");
                    break;

                case WellKnownConfigFile.ProxyCredentials:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".proxycredentials");
                    break;

                case WellKnownConfigFile.ProxyBypass:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".proxybypass");
                    break;

                case WellKnownConfigFile.Autologon:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".autologon");
                    break;

                case WellKnownConfigFile.Options:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".options");
                    break;

                case WellKnownConfigFile.Used:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".used");
                    break;
                default:
                    throw new NotSupportedException($"Unexpected well known config file: '{configFile}'");
            }

            _trace.Info($"Well known config file '{configFile}': '{path}'");
            return path;
        }

        // simple convenience factory so each suite/test gets a different trace file per run
        public Tracing GetTrace()
        {
            Tracing trace = GetTrace($"{_suiteName}_{_testName}");
            trace.Info($"Starting {_testName}");
            return trace;
        }

        public Tracing GetTrace(string name)
        {
            return _traceManager[name];
        }

        public void ShutdownAgent(ShutdownReason reason)
        {
            ArgUtil.NotNull(reason, nameof(reason));
            AgentShutdownReason = reason;
            _agentShutdownTokenSource.Cancel();
        }

        public void WritePerfCounter(string counter)
        {
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_loadContext != null)
                {
                    _loadContext.Unloading -= LoadContext_Unloading;
                    _loadContext = null;
                }
                _traceManager?.Dispose();
                try
                {
                    Directory.Delete(_tempDirectoryRoot);
                }
                catch (Exception)
                {
                    // eat exception on dispose
                }
            }
        }

        private void LoadContext_Unloading(AssemblyLoadContext obj)
        {
            if (Unloading != null)
            {
                Unloading(this, null);
            }
        }
    }
}
