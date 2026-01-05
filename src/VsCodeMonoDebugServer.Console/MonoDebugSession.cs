/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *
 *  Fork (c) 2026 Steffen Blake, Modified for Mono debugger CLI Server.
 *--------------------------------------------------------------------------------------------*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Linq;
using System.Net;
using Mono.Debugging.Client;


namespace VSCodeDebug;

public class MonoDebugSession : DebugSession
{
	private const string MONO = "mono";
	private readonly string[] MONO_EXTENSIONS = [
		".cs", ".csx",
		".cake",
		".fs", ".fsi", ".ml", ".mli", ".fsx", ".fsscript",
		".hx",
		".vb"
	];

	private const int MAX_CHILDREN = 100;
	private const int MAX_CONNECTION_ATTEMPTS = 10;
	private const int CONNECTION_ATTEMPT_INTERVAL = 500;

	private readonly AutoResetEvent _resumeEvent = new(false);
	private bool _debuggeeExecuting = false;
	private readonly Lock _lock = new();
	private Mono.Debugging.Soft.SoftDebuggerSession _session;
	private volatile bool _debuggeeKilled = true;
	private ProcessInfo _activeProcess;
	private Mono.Debugging.Client.StackFrame _activeFrame;
	private long _nextBreakpointId = 0;
	private readonly SortedDictionary<long, BreakEvent> _breakpoints = [];
	private readonly List<Catchpoint> _catchpoints = [];
	private readonly DebuggerSessionOptions _debuggerSessionOptions = new()
    {
        EvaluationOptions = EvaluationOptions.DefaultOptions
    };

	private System.Diagnostics.Process _process;
	private readonly Handles<ObjectValue[]> _variableHandles = new();
	private readonly Handles<Mono.Debugging.Client.StackFrame> _frameHandles = new();
	private ObjectValue _exception;
	private readonly Dictionary<int, Thread> _seenThreads = [];
	private bool _attachMode = false;
	private bool _terminated = false;
	private bool _stderrEOF = true;
	private bool _stdoutEOF = true;

	private dynamic exceptionOptionsFromDap;


	public MonoDebugSession() : base()
	{
		DebuggerLoggingService.CustomLogger = new CustomLogger();

        _session = new()
        {
            Breakpoints = [],
            ExceptionHandler = _ => true,
            LogWriter = (_, _) => {}
        };

        _session.TargetStopped += (sender, e) => {
			Stopped();
			SendEvent(CreateStoppedEvent("step", e.Thread));
			_resumeEvent.Set();
		};

		_session.TargetHitBreakpoint += (sender, e) => {
			Stopped();
			SendEvent(CreateStoppedEvent("breakpoint", e.Thread));
			_resumeEvent.Set();
		};

		_session.TargetExceptionThrown += (sender, e) => {
			Stopped();
			var ex = DebuggerActiveException();
			if (ex != null) {
				_exception = ex.Instance;
				SendEvent(CreateStoppedEvent("exception", e.Thread, ex.Message));
			}
			_resumeEvent.Set();
		};

		_session.TargetUnhandledException += (sender, e) => {
			Stopped();
			var ex = DebuggerActiveException();
			if (ex != null) {
				_exception = ex.Instance;
				SendEvent(CreateStoppedEvent("exception", e.Thread, ex.Message));
			}
			_resumeEvent.Set();
		};

		_session.TargetStarted += (sender, e) => {
			_activeFrame = null;
		};

		_session.TargetReady += (sender, e) => {
			SetExceptionBreakpointsFromDap(exceptionOptionsFromDap);
			_activeProcess = _session.GetProcesses().SingleOrDefault();
		};

		_session.TargetExited += (sender, e) => {

			DebuggerKill();

			_debuggeeKilled = true;

			Terminate("target exited");

			_resumeEvent.Set();
		};

		_session.TargetInterrupted += (sender, e) => {
			_resumeEvent.Set();
		};

		_session.TargetEvent += (sender, e) => {
		};

		_session.TargetThreadStarted += (sender, e) => {
			int tid = (int)e.Thread.Id;
			lock (_seenThreads) {
				_seenThreads[tid] = new Thread(tid, e.Thread.Name);
			}
			SendEvent(new ThreadEvent("started", tid));
		};

		_session.TargetThreadStopped += (sender, e) => {
			int tid = (int)e.Thread.Id;
			lock (_seenThreads) {
				_seenThreads.Remove(tid);
			}
			SendEvent(new ThreadEvent("exited", tid));
		};

		_session.OutputWriter = (isStdErr, text) => {
			SendOutput(isStdErr ? "stderr" : "stdout", text);
		};
	}

	public override void Initialize(Response response, dynamic args)
	{
		OperatingSystem os = Environment.OSVersion;
        var osSupported = false;
        osSupported |= os.Platform == PlatformID.MacOSX;
        osSupported |= os.Platform == PlatformID.Unix;
        osSupported |= os.Platform == PlatformID.Win32NT;

		if (!osSupported) {
			SendErrorResponse(
                response, 
                3000, 
                "Mono Debug is not supported on this platform ({_platform}).", 
                new { _platform = os.Platform.ToString() },
                true, 
                true
            );
			return;
		}

		SendResponse(response, new Capabilities() {
			// This debug adapter does not need the configurationDoneRequest.
			supportsConfigurationDoneRequest = false,

			// This debug adapter does not support function breakpoints.
			supportsFunctionBreakpoints = false,

			// This debug adapter doesn't support conditional breakpoints.
			supportsConditionalBreakpoints = false,

			// This debug adapter does not support a side effect free evaluate request for data hovers.
			supportsEvaluateForHovers = false,

			supportsExceptionFilterOptions = true,
			exceptionBreakpointFilters = [
				new { 
                    filter = "always", 
                    label = "All Exceptions", 
                    @default=false, 
                    supportsCondition=true, 
                    description="Break when an exception is thrown, even if it is caught later.",
                    conditionDescription = "Comma-separated list of exception types to break on"
                },
				new { 
                    filter = "uncaught", 
                    label = "Uncaught Exceptions", 
                    @default=false, 
                    supportsCondition=false, 
                    description="Breaks only on exceptions that are not handled."
                }
			]
		});

		// Mono Debug is ready to accept breakpoints immediately
		SendEvent(new InitializedEvent());
	}

	public override async void Launch(Response response, dynamic args)
	{
		_attachMode = false;

		SetExceptionBreakpoints(args.__exceptionOptions);

		// validate argument 'program'
		string programPath = GetString(args, "program");
		if (programPath == null) {
			SendErrorResponse(
                response, 3001, "Property 'program' is missing or empty.", null
            );
			return;
		}
		programPath = ConvertClientPathToDebugger(programPath);
		if (!File.Exists(programPath) && !Directory.Exists(programPath)) {
			SendErrorResponse(
                response, 
                3002, 
                "Program '{path}' does not exist.", 
                new { path = programPath }
            );
			return;
		}

		// validate argument 'cwd'
		var workingDirectory = (string)args.cwd;
		if (workingDirectory != null) {
			workingDirectory = workingDirectory.Trim();
			if (workingDirectory.Length == 0) {
				SendErrorResponse(response, 3003, "Property 'cwd' is empty.");
				return;
			}

			workingDirectory = ConvertClientPathToDebugger(workingDirectory);
			if (!Directory.Exists(workingDirectory)) {
				SendErrorResponse(
                    response, 
                    3004, 
                    "Working directory '{path}' does not exist.", 
                    new { path = workingDirectory }
                );
				return;
			}
		}

		// validate argument 'runtimeExecutable'
		var runtimeExecutable = (string)args.runtimeExecutable;
		if (runtimeExecutable != null) {
			runtimeExecutable = runtimeExecutable.Trim();
			if (runtimeExecutable.Length == 0) {
				SendErrorResponse(
                    response, 3005, "Property 'runtimeExecutable' is empty."
                );
				return;
			}

			runtimeExecutable = ConvertClientPathToDebugger(runtimeExecutable);
			if (!File.Exists(runtimeExecutable)) {
				SendErrorResponse(
                    response, 
                    3006, 
                    "Runtime executable '{path}' does not exist.", 
                    new { path = runtimeExecutable }
                );
				return;
			}
		}


		// validate argument 'env'
		Dictionary<string, string> env = [];
		var environmentVariables = args.env;
		if (environmentVariables != null) {
			foreach (var entry in environmentVariables) {
				env.Add((string)entry.Name, (string)entry.Value);
			}
		}

		const string host = "0.0.0.0";
		int port = Utilities.FindFreePort(55555);

		string mono_path = runtimeExecutable;
		if (mono_path == null) {
			if (!Utilities.IsOnPath(MONO)) {
				SendErrorResponse(
                    response, 
                    3011, 
                    "Can't find runtime '{_runtime}' on PATH.", 
                    new { _runtime = MONO }
                );
				return;
			}
			mono_path = MONO;     // try to find mono through PATH
		}


		List<string> cmdLine = [];
		bool debug = !GetBool(args, "noDebug", false);

		if (debug) {
			bool passDebugOptionsViaEnvironmentVariable = GetBool(
                args, "passDebugOptionsViaEnvironmentVariable", false
            );

			if (passDebugOptionsViaEnvironmentVariable) {
                if (env.ContainsKey("MONO_ENV_OPTIONS"))
                {
                    env["MONO_ENV_OPTIONS"] = $" --debug --debugger-agent=transport=dt_socket,server=y,address={host}:{port} " + env["MONO_ENV_OPTIONS"];
                }
                else
                {
                    env["MONO_ENV_OPTIONS"] = $" --debug --debugger-agent=transport=dt_socket,server=y,address={host}:{port}";
                }
            }
			else 
            {
				cmdLine.Add("--debug");
				cmdLine.Add($"--debugger-agent=transport=dt_socket,server=y,address={host}:{port}");
			}
		}

		if (env.Count == 0) {
			env = null;
		}

		// add 'runtimeArgs'
		if (args.runtimeArgs != null) {
			string[] runtimeArguments = args.runtimeArgs.ToObject<string[]>();
			if (runtimeArguments != null && runtimeArguments.Length > 0) {
				cmdLine.AddRange(runtimeArguments);
			}
		}

		// add 'program'
		if (workingDirectory == null) {
			// if no working dir given, we use the direct folder of the executable
			workingDirectory = Path.GetDirectoryName(programPath);
			cmdLine.Add(Path.GetFileName(programPath));
		}
		else 
        {
			// if working dir is given and if the executable is within that folder, we make the program path relative to the working dir
			cmdLine.Add(Utilities.MakeRelativePath(workingDirectory, programPath));
		}

		// add 'args'
		if (args.args != null) {
			string[] arguments = args.args.ToObject<string[]>();
			if (arguments != null && arguments.Length > 0) {
				cmdLine.AddRange(arguments);
			}
		}

		// what console?
		var console = GetString(args, "console", null);
		if (console == null) {
			// continue to read the deprecated "externalConsole" attribute
			bool externalConsole = GetBool(args, "externalConsole", false);
			if (externalConsole) {
				console = "externalTerminal";
			}
		}

		if (console == "externalTerminal" || console == "integratedTerminal") {

			cmdLine.Insert(0, mono_path);
			var termArgs = new {
				kind = console == "integratedTerminal" ? "integrated" : "external",
				title = "Node Debug Console",
				cwd = workingDirectory,
				args = cmdLine.ToArray(),
				env
			};

			var resp = await SendRequest("runInTerminal", termArgs);
			if (!resp.success) {
				SendErrorResponse(response, 3011, "Cannot launch debug target in terminal ({_error}).", new { _error = resp.message });
				return;
			}

		} 
        else 
        { 
            // internalConsole
			_process = new System.Diagnostics.Process();
			_process.StartInfo.CreateNoWindow = true;
			_process.StartInfo.UseShellExecute = false;
			_process.StartInfo.WorkingDirectory = workingDirectory;
			_process.StartInfo.FileName = mono_path;
			_process.StartInfo.Arguments = Utilities.ConcatArgs([.. cmdLine]);

			_stdoutEOF = false;
			_process.StartInfo.RedirectStandardOutput = true;
			_process.OutputDataReceived += (_, e) => 
            {
				if (e.Data == null) {
					_stdoutEOF = true;
				}
				SendOutput("stdout", e.Data);
			};

			_stderrEOF = false;
			_process.StartInfo.RedirectStandardError = true;
			_process.ErrorDataReceived += (_, e) => 
            {
				if (e.Data == null) {
					_stderrEOF = true;
				}
				SendOutput("stderr", e.Data);
			};

			_process.EnableRaisingEvents = true;
			_process.Exited += (_, _) => Terminate("runtime process exited");

            // we cannot set the env vars on the process StartInfo because we need to set StartInfo.UseShellExecute to true at the same time.
            // instead we set the env vars on MonoDebug itself because we know that MonoDebug lives as long as a debug session.
			if (env != null) {
				foreach (var entry in env) {
					Environment.SetEnvironmentVariable(entry.Key, entry.Value);
				}
			}

			var cmd = string.Format("{0} {1}", mono_path, _process.StartInfo.Arguments);
			SendOutput("console", cmd);

			try {
				_process.Start();
				_process.BeginOutputReadLine();
				_process.BeginErrorReadLine();
			}
			catch (Exception e) {
				SendErrorResponse(response, 3012, "Can't launch terminal ({reason}).", new { reason = e.Message });
				return;
			}
		}

		if (debug) {
			Connect(IPAddress.Parse(host), port);
		}

		SendResponse(response);

		if (_process == null && !debug) {
			// we cannot track mono runtime process so terminate this session
			Terminate("cannot track mono runtime");
		}
	}

	public override void Attach(Response response, dynamic args)
	{
		_attachMode = true;

		SetExceptionBreakpoints(args.__exceptionOptions);

		// validate argument 'address'
		var host = GetString(args, "address");
		if (host == null) {
			SendErrorResponse(response, 3007, "Property 'address' is missing or empty.");
			return;
		}

		// validate argument 'port'
		var port = GetInt(args, "port", -1);
		if (port == -1) {
			SendErrorResponse(response, 3008, "Property 'port' is missing.");
			return;
		}

		IPAddress address = Utilities.ResolveIPAddress(host);
		if (address == null) {
			SendErrorResponse(
                response, 
                3013, 
                "Invalid address '{address}'.", 
                new { address }
            );
			return;
		}

		Connect(address, port);

		SendResponse(response);
	}

	public override void Disconnect(Response response, dynamic args)
	{
		if (_attachMode) {

			lock (_lock) {
				if (_session != null) {
					_debuggeeExecuting = true;
					_breakpoints.Clear();
					_session.Breakpoints.Clear();
					_session.Continue();
					_session = null;
				}
			}

		} else {
			// Let's not leave dead Mono processes behind...
			if (_process != null) {
				_process.Kill();
				_process = null;
			} else {
				PauseDebugger();
				DebuggerKill();

				while (!_debuggeeKilled) {
					System.Threading.Thread.Sleep(10);
				}
			}
		}

		SendResponse(response);
	}

	public override void Continue(Response response, dynamic args)
	{
		WaitForSuspend();
		SendResponse(response);
		lock (_lock) {
			if (_session != null && !_session.IsRunning && !_session.HasExited) {
				_session.Continue();
				_debuggeeExecuting = true;
			}
		}
	}

	public override void Next(Response response, dynamic args)
	{
		WaitForSuspend();
		SendResponse(response);
		lock (_lock) {
			if (_session != null && !_session.IsRunning && !_session.HasExited) {
				_session.NextLine();
				_debuggeeExecuting = true;
			}
		}
	}

	public override void StepIn(Response response, dynamic args)
	{
		WaitForSuspend();
		SendResponse(response);
		lock (_lock) {
			if (_session != null && !_session.IsRunning && !_session.HasExited) {
				_session.StepLine();
				_debuggeeExecuting = true;
			}
		}
	}

	public override void StepOut(Response response, dynamic args)
	{
		WaitForSuspend();
		SendResponse(response);
		lock (_lock) {
			if (_session != null && !_session.IsRunning && !_session.HasExited) {
				_session.Finish();
				_debuggeeExecuting = true;
			}
		}
	}

	public override void Pause(Response response, dynamic args)
	{
		SendResponse(response);
		PauseDebugger();
	}

	public override void SetExceptionBreakpoints(Response response, dynamic args)
	{
		if (args.filterOptions != null)
		{
			if (_activeProcess != null)
				SetExceptionBreakpointsFromDap(args.filterOptions);
			else
				exceptionOptionsFromDap = args.filterOptions;
		}
		else
			SetExceptionBreakpoints(args.exceptionOptions);
		SendResponse(response);
	}

	public override void SetBreakpoints(Response response, dynamic args)
	{
		string path = null;
		if (args.source != null) {
			string p = (string)args.source.path;
			if (p != null && p.Trim().Length > 0) {
				path = p;
			}
		}
		if (path == null) {
			SendErrorResponse(response, 3010, "setBreakpoints: property 'source' is empty or misformed", null, false, true);
			return;
		}
		path = ConvertClientPathToDebugger(path);

		if (!HasMonoExtension(path)) {
			// we only support breakpoints in files mono can handle
			SendResponse(response, new SetBreakpointsResponseBody());
			return;
		}

		var clientLines = args.lines.ToObject<int[]>();
		HashSet<int> lin = new HashSet<int>();
		for (int i = 0; i < clientLines.Length; i++) {
			lin.Add(ConvertClientLineToDebugger(clientLines[i]));
		}

		// find all breakpoints for the given path and remember their id and line number
		var bpts = new List<Tuple<int, int>>();
		foreach (var be in _breakpoints) {
            if (be.Value is Mono.Debugging.Client.Breakpoint bp && bp.FileName == path)
            {
                bpts.Add(new Tuple<int, int>((int)be.Key, bp.Line));
            }
        }

		HashSet<int> lin2 = [];
		foreach (var bpt in bpts) {
			if (lin.Contains(bpt.Item2)) {
				lin2.Add(bpt.Item2);
			}
			else {
                // Program.Log("cleared bpt #{0} for line {1}", bpt.Item1, bpt.Item2);
                if (_breakpoints.TryGetValue(bpt.Item1, out BreakEvent b))
                {
                    _breakpoints.Remove(bpt.Item1);
                    _session.Breakpoints.Remove(b);
                }
            }
		}

		for (int i = 0; i < clientLines.Length; i++) {
			var l = ConvertClientLineToDebugger(clientLines[i]);
			if (!lin2.Contains(l)) {
				var id = _nextBreakpointId++;
				_breakpoints.Add(id, _session.Breakpoints.Add(path, l));
				// Program.Log("added bpt #{0} for line {1}", id, l);
			}
		}

		var breakpoints = new List<Breakpoint>();
		foreach (var l in clientLines) {
			breakpoints.Add(new Breakpoint(true, l));
		}

		SendResponse(response, new SetBreakpointsResponseBody(breakpoints));
	}

	public override void StackTrace(Response response, dynamic args)
	{
		int maxLevels = GetInt(args, "levels", 10);
		int threadReference = GetInt(args, "threadId", 0);

		WaitForSuspend();

		ThreadInfo thread = DebuggerActiveThread();
		if (thread.Id != threadReference) {
			// Program.Log("stackTrace: unexpected: active thread should be the one requested");
			thread = FindThread(threadReference);
			thread?.SetActive();
		}

		var stackFrames = new List<StackFrame>();
		int totalFrames = 0;

		var bt = thread.Backtrace;
		if (bt != null && bt.FrameCount >= 0) {

			totalFrames = bt.FrameCount;

			for (var i = 0; i < Math.Min(totalFrames, maxLevels); i++) {

				var frame = bt.GetFrame(i);

				string path = frame.SourceLocation.FileName;

				var hint = "subtle";
				Source source = null;
				if (!string.IsNullOrEmpty(path)) {
					string sourceName = Path.GetFileName(path);
					if (!string.IsNullOrEmpty(sourceName)) {
						if (File.Exists(path)) {
							source = new Source(sourceName, ConvertDebuggerPathToClient(path), 0, "normal");
							hint = "normal";
						} else {
							source = new Source(sourceName, null, 1000, "deemphasize");
						}
					}
				}

				var frameHandle = _frameHandles.Create(frame);
				string name = frame.SourceLocation.MethodName;
				int line = frame.SourceLocation.Line;
				stackFrames.Add(new StackFrame(frameHandle, name, source, ConvertDebuggerLineToClient(line), 0, hint));
			}
		}

		SendResponse(response, new StackTraceResponseBody(stackFrames, totalFrames));
	}

	public override void Source(Response response, dynamic arguments) {
		SendErrorResponse(response, 1020, "No source available");
	}

	public override void Scopes(Response response, dynamic args) {

		int frameId = GetInt(args, "frameId", 0);
		var frame = _frameHandles.Get(frameId, null);

		var scopes = new List<Scope>();

		if (frame.Index == 0 && _exception != null) {
			scopes.Add(new Scope("Exception", _variableHandles.Create([_exception])));
		}

		var locals = new[] { frame.GetThisReference() }.Concat(frame.GetParameters()).Concat(frame.GetLocalVariables()).Where(x => x != null).ToArray();
		if (locals.Length > 0) {
			scopes.Add(new Scope("Local", _variableHandles.Create(locals)));
		}

		SendResponse(response, new ScopesResponseBody(scopes));
	}

	public override void Variables(Response response, dynamic args)
	{
		int reference = GetInt(args, "variablesReference", -1);
		if (reference == -1) {
			SendErrorResponse(response, 3009, "variables: property 'variablesReference' is missing", null, false, true);
			return;
		}

		WaitForSuspend();
		var variables = new List<Variable>();

        if (_variableHandles.TryGet(reference, out ObjectValue[] children))
        {
            if (children != null && children.Length > 0)
            {
                bool more = false;
                if (children.Length > MAX_CHILDREN)
                {
                    children = [.. children.Take(MAX_CHILDREN)];
                    more = true;
                }

                if (children.Length < 20)
                {
                    // Wait for all values at once.
                    WaitHandle.WaitAll([.. children.Select(x => x.WaitHandle)]);
                    foreach (var v in children)
                    {
                        variables.Add(CreateVariable(v));
                    }
                }
                else
                {
                    foreach (var v in children)
                    {
                        v.WaitHandle.WaitOne();
                        variables.Add(CreateVariable(v));
                    }
                }

                if (more)
                {
                    variables.Add(new Variable("...", null, null));
                }
            }
        }

        SendResponse(response, new VariablesResponseBody(variables));
	}

	public override void Threads(Response response, dynamic args)
	{
		var threads = new List<Thread>();
		var process = _activeProcess;
		if (process != null) {
			Dictionary<int, Thread> d;
			lock (_seenThreads) {
				d = new Dictionary<int, Thread>(_seenThreads);
			}
			foreach (var t in process.GetThreads()) {
				int tid = (int)t.Id;
				d[tid] = new Thread(tid, t.Name);
			}
			threads = [.. d.Values];
		}
		SendResponse(response, new ThreadsResponseBody(threads));
	}

	public override void Evaluate(Response response, dynamic args)
	{
		string error = null;

		var expression = GetString(args, "expression");
		if (expression == null) {
			error = "expression missing";
		} else {
			int frameId = GetInt(args, "frameId", -1);
			var frame = _frameHandles.Get(frameId, null);
			if (frame != null) {
				if (frame.ValidateExpression(expression)) {
					var val = frame.GetExpressionValue(expression, _debuggerSessionOptions.EvaluationOptions);
					val.WaitHandle.WaitOne();

					var flags = val.Flags;
					if (flags.HasFlag(ObjectValueFlags.Error) || flags.HasFlag(ObjectValueFlags.NotSupported)) {
						error = val.DisplayValue;
						if (error.IndexOf("reference not available in the current evaluation context") > 0) {
							error = "not available";
						}
					}
					else if (flags.HasFlag(ObjectValueFlags.Unknown)) {
						error = "invalid expression";
					}
					else if (flags.HasFlag(ObjectValueFlags.Object) && flags.HasFlag(ObjectValueFlags.Namespace)) {
						error = "not available";
					}
					else {
						int handle = 0;
						if (val.HasChildren) {
							handle = _variableHandles.Create(val.GetAllChildren());
						}
						SendResponse(response, new EvaluateResponseBody(val.DisplayValue, handle));
						return;
					}
				}
				else {
					error = "invalid expression";
				}
			}
			else {
				error = "no active stackframe";
			}
		}
		SendErrorResponse(
            response, 
            3014, 
            "Evaluate request failed ({_reason}).", 
            new { _reason = error } 
        );
	}

	//---- private ------------------------------------------

	private void SetExceptionBreakpointsFromDap(dynamic exceptionOptions)
	{
		if (exceptionOptions != null) {
			var exceptions = exceptionOptions.ToObject<dynamic[]>();
			for (int i = 0; i < exceptions.Length; i++) {
				var exception = exceptions[i];

				bool caught = exception.filterId == "always" ? true : false;
				if (exception.condition != null && exception.condition != "") {
					string[] conditionNames = exception.condition.ToString().Split(',');
					foreach (var conditionName in conditionNames)
						_session.EnableException(conditionName, caught);
				}
				else {
					_session.EnableException("System.Exception", caught);
				}
			}
		}
	}
	private void SetExceptionBreakpoints(dynamic exceptionOptions)
	{
		if (exceptionOptions != null) {

			// clear all existig catchpoints
			foreach (var cp in _catchpoints) {
				_session.Breakpoints.Remove(cp);
			}
			_catchpoints.Clear();

			var exceptions = exceptionOptions.ToObject<dynamic[]>();
			for (int i = 0; i < exceptions.Length; i++) {

				var exception = exceptions[i];

				string exName = null;
				string exBreakMode = exception.breakMode;

				if (exception.path != null) {
					var paths = exception.path.ToObject<dynamic[]>();
					var path = paths[0];
					if (path.names != null) {
						var names = path.names.ToObject<dynamic[]>();
						if (names.Length > 0) {
							exName = names[0];
						}
					}
				}

				if (exName != null && exBreakMode == "always") {
					_catchpoints.Add(_session.Breakpoints.AddCatchpoint(exName));
				}
			}
		}
	}

	private void SendOutput(string category, string data) {
		if (!string.IsNullOrEmpty(data)) {
			if (!data.EndsWith('\n')) {
				data += '\n';
			}
			SendEvent(new OutputEvent(category, data));
		}
	}

	private void Terminate(string _) {
		if (!_terminated) {
			// wait until we've seen the end of stdout and stderr
			for (int i = 0; i < 100 && (_stdoutEOF == false || _stderrEOF == false); i++) {
				System.Threading.Thread.Sleep(100);
			}

			SendEvent(new TerminatedEvent());

			_terminated = true;
			_process = null;
		}
	}

	private static StoppedEvent CreateStoppedEvent(
        string reason, ThreadInfo ti, string text = null
    )
	{
		return new StoppedEvent((int)ti.Id, reason, text);
	}

	private ThreadInfo FindThread(int threadReference)
	{
		if (_activeProcess != null) {
			foreach (var t in _activeProcess.GetThreads()) {
				if (t.Id == threadReference) {
					return t;
				}
			}
		}
		return null;
	}

	private void Stopped()
	{
		_exception = null;
		_variableHandles.Reset();
		_frameHandles.Reset();
	}

	private Variable CreateVariable(ObjectValue v)
	{
		var dv = v.DisplayValue;

		if (dv.StartsWith('{') && dv.EndsWith('}')) {
			dv = dv[1..^1];
		}

        var variablesReference =  0;
        if (v.HasChildren)
        {
           variablesReference =  _variableHandles.Create(v.GetAllChildren());
        }

		return new Variable(
            v.Name, 
            dv, 
            v.TypeName, 
            variablesReference
        );
	}

	private bool HasMonoExtension(string path)
	{
        return MONO_EXTENSIONS.Any(e => path.EndsWith(e));
	}

	private static bool GetBool(dynamic container, string propertyName, bool dflt = false)
	{
		try {
			return (bool)container[propertyName];
		}
		catch (Exception) {
			// ignore and return default value
		}
		return dflt;
	}

	private static int GetInt(dynamic container, string propertyName, int dflt = 0)
	{
		try {
			return (int)container[propertyName];
		}
		catch (Exception) {
			// ignore and return default value
		}
		return dflt;
	}

	private static string GetString(dynamic args, string property, string dflt = null)
	{
		var s = (string)args[property];
		if (s == null) {
			return dflt;
		}
		s = s.Trim();
		if (s.Length == 0) {
			return dflt;
		}
		return s;
	}

	//-----------------------

	private void WaitForSuspend()
	{
		if (_debuggeeExecuting) {
			_resumeEvent.WaitOne();
			_debuggeeExecuting = false;
		}
	}

	private ThreadInfo DebuggerActiveThread()
	{
		lock (_lock) {
			return _session?.ActiveThread;
		}
	}

	private Backtrace DebuggerActiveBacktrace() {
		return DebuggerActiveThread()?.Backtrace;
	}

	private Mono.Debugging.Client.StackFrame DebuggerActiveFrame() {
		if (_activeFrame != null)
			return _activeFrame;

		var bt = DebuggerActiveBacktrace();
		if (bt != null)
        {
			return _activeFrame = bt.GetFrame(0);
        }

		return null;
	}

	private ExceptionInfo DebuggerActiveException() {
		var bt = DebuggerActiveBacktrace();
		return bt?.GetFrame(0).GetException();
	}

	private void Connect(IPAddress address, int port)
	{
		lock (_lock) {
			_debuggeeKilled = false;

			var args0 = new Mono.Debugging.Soft.SoftDebuggerConnectArgs(
                string.Empty, 
                address, 
                port
            ) {
				MaxConnectionAttempts = MAX_CONNECTION_ATTEMPTS,
				TimeBetweenConnectionAttempts = CONNECTION_ATTEMPT_INTERVAL
			};

			_session.Run(
                new Mono.Debugging.Soft.SoftDebuggerStartInfo(args0), 
                _debuggerSessionOptions
            );

			_debuggeeExecuting = true;
		}
	}

	private void PauseDebugger()
	{
		lock (_lock) {
			if (_session != null && _session.IsRunning)
            {
				_session.Stop();
            }
		}
	}

	private void DebuggerKill()
	{
		lock (_lock) {
			if (_session != null) {

				_debuggeeExecuting = true;

				if (!_session.HasExited)
                {
					_session.Exit();
                }

				_session.Dispose();
				_session = null;
			}
		}
	}
}
