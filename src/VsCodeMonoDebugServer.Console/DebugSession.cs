/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *
 *  Fork (c) 2026 Steffen Blake, Modified for Mono debugger CLI Server.
 *--------------------------------------------------------------------------------------------*/
using System;
using System.Collections.Generic;
using Newtonsoft.Json;


namespace VSCodeDebug;

// ---- Types -------------------------------------------------------------------------

public class Message(
    int id,
    string format,
    dynamic variables = null,
    bool user = true,
    bool telemetry = false
)
{
    [JsonProperty("id")]
    public int Id { get; } = id;
    
    [JsonProperty("format")]
    public string Format { get; } = format;
    
    [JsonProperty("variables")]
    public dynamic Variables { get; } = variables;
    
    [JsonProperty("showUser")]
    public dynamic ShowUser { get; } = user;

    [JsonProperty("sendTelemetry")]
    public dynamic SendTelemetry { get; } = telemetry;
}

public class StackFrame(
    int id, string name, Source source, int line, int column, string hint
)
{
    [JsonProperty("id")]
    public int Id { get; } = id;
    
    [JsonProperty("source")]
    public Source Source { get; } = source;

    // These should NEVER be negative
    [JsonProperty("line")]
    public int Line { get; } = Math.Max(0, line);
    
    [JsonProperty("column")]
    public int Column { get; } = Math.Max(0, column);

    [JsonProperty("name")]
    public string Name { get; } = name;
    
    [JsonProperty("presentationHint")]
    public string PresentationHint { get; } = hint;
}

public class Scope(
    string name, int variablesReference, bool expensive = false
)
{
    [JsonProperty("name")]
    public string Name { get; } = name;
    
    [JsonProperty("variablesReference")]
    public int VariablesReference { get; } = variablesReference;
    
    [JsonProperty("expensive")]
    public bool Expensive { get; } = expensive;
}

public class Variable(
    string name, string value, string type, int variablesReference = 0
)
{
    [JsonProperty("name")]
    public string Name { get; } = name;
    
    [JsonProperty("value")]
    public string Value { get; } = value;
    
    [JsonProperty("type")]
    public string Type { get; } = type;
    
    [JsonProperty("expensive")]
    public int VariablesReference { get; } = variablesReference;
}

public class Thread
{
    [JsonProperty("id")]
	public int Id { get; }
    
    [JsonProperty("name")]
	public string Name { get; }

	public Thread(int id, string name) {
		Id = id;
		if (name == null || name.Length == 0) 
        {
			Name = string.Format("Thread #{0}", id);
		} 
        else 
        {
			Name = name;
		}
	}
}

public class Source(
    string name, string path, int sourceReference, string hint
)
{
    [JsonProperty("name")]
    public string Name { get; } = name;
    
    [JsonProperty("path")]
    public string Path { get; } = path;
    
    [JsonProperty("sourceReference")]
    public int SourceReference { get; } = sourceReference;
    
    [JsonProperty("presentationHint")]
    public string PresentationHint { get; } = hint;
}

public class Breakpoint(bool verified, int line)
{
    [JsonProperty("verified")]
    public bool Verified { get; } = verified;
    
    [JsonProperty("line")]
    public int Line { get; } = line;
}

// ---- Events -------------------------------------------------------------------------

public class InitializedEvent() : Event("initialized");

public class StoppedEvent(
    int tid, 
    string reasn, 
    string txt = null
) : Event("stopped", 
    new {
        threadId = tid,
        reason = reasn,
        text = txt
    }
);

public class ExitedEvent(
    int exCode
) : Event(
    "exited",
    new { exitCode = exCode } 
);

public class TerminatedEvent() : Event("terminated");

public class ThreadEvent(
    string reasn, int tid
) : Event(
    "thread", 
    new {
        reason = reasn,
        threadId = tid
    }
);

public class OutputEvent(
    string cat, string outpt
) : Event(
    "output", 
    new {
        category = cat,
        output = outpt
    }
);

// ---- Response -------------------------------------------------------------------------

public class Capabilities : ResponseBody {

	public bool supportsConfigurationDoneRequest;
	public bool supportsFunctionBreakpoints;
	public bool supportsConditionalBreakpoints;
	public bool supportsEvaluateForHovers;
	public bool supportsExceptionFilterOptions;
	public dynamic[] exceptionBreakpointFilters;
}

public class ErrorResponseBody(
    Message error
) : ResponseBody
{
    [JsonProperty("error")]
    public Message Error { get; } = error;
}

public class StackTraceResponseBody(
    List<StackFrame> frames, int total
) : ResponseBody
{
    [JsonProperty("stackFrames")]
    public StackFrame[] StackFrames { get; } = [.. frames];
    
    [JsonProperty("totalFrames")]
    public int TotalFrames { get; } = total;
}

public class ScopesResponseBody(
    List<Scope> scps
) : ResponseBody
{
    [JsonProperty("scopes")]
    public Scope[] Scopes { get; } = [.. scps];
}

public class VariablesResponseBody(
    List<Variable> vars
) : ResponseBody
{
    [JsonProperty("variables")]
    public Variable[] Variables { get; } = [.. vars];
}

public class ThreadsResponseBody(
    List<Thread> ths
) : ResponseBody
{
    [JsonProperty("threads")]
    public Thread[] Threads { get; } = [.. ths];
}

public class EvaluateResponseBody(
    string value, int reff = 0
) : ResponseBody
{
    [JsonProperty("result")]
    public string Result { get; } = value;
    
    [JsonProperty("variablesReference")]
    public int VariablesReference { get; } = reff;
}

public class SetBreakpointsResponseBody(
    List<Breakpoint> bpts = null
) : ResponseBody
{
    [JsonProperty("breakpoints")]
	public Breakpoint[] Breakpoints { get; } = bpts == null ? [] : [.. bpts];
}

// ---- The Session --------------------------------------------------------

public abstract class DebugSession : ProtocolServer
{
	private bool _clientLinesStartAt1 = true;
	private bool _clientPathsAreURI = true;

	public void SendResponse(Response response, dynamic body = null)
	{
		if (body != null) {
			response.SetBody(body);
		}
		SendMessage(response);
	}

	public void SendErrorResponse(
        Response response, 
        int id, 
        string format, 
        dynamic arguments = null, 
        bool user = true, 
        bool telemetry = false
    )
	{
		var msg = new Message(id, format, arguments, user, telemetry);
		var message = Utilities.ExpandVariables(msg.Format, msg.Variables);
		response.SetErrorBody(message, new ErrorResponseBody(msg));
		SendMessage(response);
	}

	protected override void DispatchRequest(
        string command, dynamic args, Response response
    )
	{
		args ??= new { };

		try {
			switch (command) {

			case "initialize":
				if (args.linesStartAt1 != null) {
					_clientLinesStartAt1 = (bool)args.linesStartAt1;
				}
				var pathFormat = (string)args.pathFormat;
				if (pathFormat != null) {
					switch (pathFormat) {
					case "uri":
						_clientPathsAreURI = true;
						break;
					case "path":
						_clientPathsAreURI = false;
						break;
					default:
						SendErrorResponse(response, 1015, "initialize: bad value '{_format}' for pathFormat", new { _format = pathFormat });
						return;
					}
				}
				Initialize(response, args);
				break;

			case "launch":
				Launch(response, args);
				break;

			case "attach":
				Attach(response, args);
				break;

			case "disconnect":
				Disconnect(response, args);
				break;

			case "next":
				Next(response, args);
				break;

			case "continue":
				Continue(response, args);
				break;

			case "stepIn":
				StepIn(response, args);
				break;

			case "stepOut":
				StepOut(response, args);
				break;

			case "pause":
				Pause(response, args);
				break;

			case "stackTrace":
				StackTrace(response, args);
				break;

			case "scopes":
				Scopes(response, args);
				break;

			case "variables":
				Variables(response, args);
				break;

			case "source":
				Source(response, args);
				break;

			case "threads":
				Threads(response, args);
				break;

			case "setBreakpoints":
				SetBreakpoints(response, args);
				break;

			case "setFunctionBreakpoints":
				SetFunctionBreakpoints(response, args);
				break;

			case "setExceptionBreakpoints":
				SetExceptionBreakpoints(response, args);
				break;

			case "evaluate":
				Evaluate(response, args);
				break;

			default:
				SendErrorResponse(response, 1014, "unrecognized request: {_request}", new { _request = command });
				break;
			}
		}
		catch (Exception e) {
			SendErrorResponse(response, 1104, "error while processing request '{_request}' (exception: {_exception})", new { _request = command, _exception = e.Message });
		}

		if (command == "disconnect") {
			Stop();
		}
	}

	public abstract void Initialize(Response response, dynamic args);

	public abstract void Launch(Response response, dynamic arguments);

	public abstract void Attach(Response response, dynamic arguments);

	public abstract void Disconnect(Response response, dynamic arguments);

	public virtual void SetFunctionBreakpoints(Response response, dynamic arguments)
	{
	}

	public virtual void SetExceptionBreakpoints(Response response, dynamic arguments)
	{
	}

	public abstract void SetBreakpoints(Response response, dynamic arguments);

	public abstract void Continue(Response response, dynamic arguments);

	public abstract void Next(Response response, dynamic arguments);

	public abstract void StepIn(Response response, dynamic arguments);

	public abstract void StepOut(Response response, dynamic arguments);

	public abstract void Pause(Response response, dynamic arguments);

	public abstract void StackTrace(Response response, dynamic arguments);

	public abstract void Scopes(Response response, dynamic arguments);

	public abstract void Variables(Response response, dynamic arguments);

	public abstract void Source(Response response, dynamic arguments);

	public abstract void Threads(Response response, dynamic arguments);

	public abstract void Evaluate(Response response, dynamic arguments);

	// protected

	protected int ConvertDebuggerLineToClient(int line)
	{
		return _clientLinesStartAt1 ? line : line - 1;
	}

	protected int ConvertClientLineToDebugger(int line)
	{
		return _clientLinesStartAt1 ? line : line + 1;
	}

	protected string ConvertDebuggerPathToClient(string path)
	{
		if (_clientPathsAreURI) {
			try {
				var uri = new Uri(path);
				return uri.AbsoluteUri;
			}
			catch {
				return null;
			}
		}
		else {
			return path;
		}
	}

	protected string ConvertClientPathToDebugger(string clientPath)
	{
		if (clientPath == null) {
			return null;
		}

		if (_clientPathsAreURI) {
			if (Uri.IsWellFormedUriString(clientPath, UriKind.Absolute)) {
				Uri uri = new(clientPath);
				return uri.LocalPath;
			}

			Program.Log("path not well formed: '{0}'", clientPath);
			return null;
		}
		else {
			return clientPath;
		}
	}
}
