/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *
 *  Fork (c) 2026 Steffen Blake, Modified for Mono debugger CLI Server.
 *--------------------------------------------------------------------------------------------*/
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace VSCodeDebug;

internal class Program
{
	const int DEFAULT_PORT = 4711;

	private static bool trace_requests;
	private static bool trace_responses;
	static string LOG_FILE_PATH = null;

	private static void Main(string[] argv)
	{
		int port = -1;

		// parse command line arguments
		foreach (var a in argv) {
			switch (a) {
			case "--trace":
				trace_requests = true;
				break;
			case "--trace=response":
				trace_requests = true;
				trace_responses = true;
				break;
			case "--server":
				port = DEFAULT_PORT;
				break;
			default:
				if (a.StartsWith("--server=")) {
					if (!int.TryParse(a.AsSpan("--server=".Length), out port)) {
						port = DEFAULT_PORT;
					}
				}
				else if( a.StartsWith("--log-file=")) {
					LOG_FILE_PATH = a["--log-file=".Length..];
				}
				break;
			}
		}

		if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("mono_debug_logfile")) == false) {
			LOG_FILE_PATH = Environment.GetEnvironmentVariable("mono_debug_logfile");
			trace_requests = true;
			trace_responses = true;
		}

		if (port > 0) {
			// TCP/IP server
			Log("waiting for debug protocol on port " + port);
			RunServer(port);
		} else {
			// stdin/stdout
			Log("waiting for debug protocol on stdin/stdout");
			RunSession(Console.OpenStandardInput(), Console.OpenStandardOutput());
		}
	}

	static StreamWriter logFile;

	public static void Log(bool predicate, string format, params object[] data)
	{
		if (predicate)
		{
			Log(format, data);
		}
	}
	
	public static void Log(string format, params object[] data)
	{
		try
		{
			Console.Error.WriteLine(format, data);

			if (LOG_FILE_PATH != null)
			{
				logFile ??= File.CreateText(LOG_FILE_PATH);

				string msg = string.Format(format, data);
				logFile.WriteLine(
                    string.Format("{0} {1}", DateTime.UtcNow.ToLongTimeString(), msg)
                );
			}
		}
		catch (Exception ex)
		{
			if (LOG_FILE_PATH != null)
			{
				try
				{
					File.WriteAllText(LOG_FILE_PATH + ".err", ex.ToString());
				}
				catch
				{
				}
			}

			throw;
		}
	}

	private static void RunSession(Stream inputStream, Stream outputStream)
	{
        DebugSession debugSession = new MonoDebugSession
        {
            TRACE = trace_requests,
            TRACE_RESPONSE = trace_responses
        };

        debugSession.Start(inputStream, outputStream).Wait();

		if (logFile!=null)
		{
			logFile.Flush();
			logFile.Close();
			logFile = null;
		}
	}

	private static void RunServer(int port)
	{
		TcpListener serverSocket = new(IPAddress.Parse("0.0.0.0"), port);
		serverSocket.Start();

		new System.Threading.Thread(() => {
			while (true) {
				var clientSocket = serverSocket.AcceptSocket();
				if (clientSocket != null) {
					Log(">> accepted connection from client");

					new System.Threading.Thread(() => {
						using (var networkStream = new NetworkStream(clientSocket)) {
							try {
								RunSession(networkStream, networkStream);
							}
							catch (Exception e) {
								Console.Error.WriteLine("Exception: " + e);
							}
						}
						clientSocket.Close();
						Console.Error.WriteLine(">> client connection closed");
					}).Start();
				}
			}
		}).Start();
	}
}
